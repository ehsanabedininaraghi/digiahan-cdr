using System.Diagnostics;
using DigiAhan.CDR.Receiver.Models;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class IntegrationSchedulerService
{
    private readonly IntegrationSchedulerRepository _repository;
    private readonly LegacyAccountingBridgeRunner _accounting;
    private readonly CustomerIdentityReconcileService _identities;
    private readonly DidarApiSyncService _didarApi;
    private readonly CustomerMappingService _mappings;
    private readonly SystemHealthService _health;
    private readonly DatabaseMaintenanceService _maintenance;
    private readonly InvoiceNotificationRepository _invoiceNotifications;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<IntegrationSchedulerService> _logger;

    public IntegrationSchedulerService(
        IntegrationSchedulerRepository repository,
        LegacyAccountingBridgeRunner accounting,
        CustomerIdentityReconcileService identities,
        DidarApiSyncService didarApi,
        CustomerMappingService mappings,
        SystemHealthService health,
        DatabaseMaintenanceService maintenance,
        InvoiceNotificationRepository invoiceNotifications,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<IntegrationSchedulerService> logger)
    {
        _repository = repository;
        _accounting = accounting;
        _identities = identities;
        _didarApi = didarApi;
        _mappings = mappings;
        _health = health;
        _maintenance = maintenance;
        _invoiceNotifications = invoiceNotifications;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    public async Task RunDueAsync(CancellationToken ct)
    {
        foreach (var jobKey in await _repository.GetDueJobKeysAsync(ct))
            await RunAsync(jobKey, false, ct);
    }

    public async Task<IReadOnlyList<IntegrationRunNowResult>> RunAllEnabledAsync(CancellationToken ct)
    {
        var jobs = await _repository.GetAllAsync(ct);
        var results = new List<IntegrationRunNowResult>();

        foreach (var job in jobs.Where(x => x.IsEnabled))
        {
            var started = await RunAsync(job.JobKey, true, ct);
            var current = (await _repository.GetAllAsync(ct)).First(x => x.JobKey == job.JobKey);
            results.Add(new IntegrationRunNowResult(
                job.JobKey,
                job.DisplayName,
                started,
                current.LastStatus ?? (started ? "SUCCESS" : "SKIPPED"),
                current.LastError));
        }

        return results;
    }

    public async Task<bool> RunAsync(string jobKey, bool force, CancellationToken ct)
    {
        jobKey = jobKey.Trim().ToUpperInvariant();
        if (force) await _repository.ForceDueAsync(jobKey, ct);
        var runId = await _repository.TryStartAsync(jobKey, ct);
        if (runId is null) return false;

        var sw = Stopwatch.StartNew();
        try
        {
            var detail = await ExecuteAsync(jobKey, ct);
            await _repository.FinishAsync(runId.Value, jobKey, true, sw.ElapsedMilliseconds, detail, CancellationToken.None);
            _logger.LogInformation("Integration job succeeded. Job={Job} DurationMs={DurationMs}", jobKey, sw.ElapsedMilliseconds);
            return true;
        }
        catch (Exception ex)
        {
            await _repository.FinishAsync(runId.Value, jobKey, false, sw.ElapsedMilliseconds, ex.Message, CancellationToken.None);
            _logger.LogError(ex, "Integration job failed. Job={Job} DurationMs={DurationMs}", jobKey, sw.ElapsedMilliseconds);
            return false;
        }
    }

    private async Task<string> ExecuteAsync(string jobKey, CancellationToken ct)
    {
        switch (jobKey)
        {
            case "ACCOUNTING":
            {
                if (!_accounting.IsConfigured) return "Accounting is disabled.";
                var days = Math.Clamp(_configuration.GetValue("DataGathering:IncrementalAccountingDays", 7), 1, 31);
                var result = await _accounting.RunAsync(days, ct);
                if (result.Status != "SUCCESS") throw new InvalidOperationException(result.Error + Environment.NewLine + result.Output);
                var mapping = await _mappings.ReconcileAsync(ct);
                var notifications = await _invoiceNotifications.DiscoverAsync(ct);
                return $"Incremental rolling window: {days} days. Mapping: linked={mapping.LinkedCodes}, pending={mapping.UnmappedCodes + mapping.ConflictCodes}. Notifications: scanned={notifications.Scanned}, ready={notifications.Ready}, needsIdentity={notifications.NeedsIdentity}, needsPhone={notifications.NeedsPhone}.";
            }
            case "DIDAR_IDENTITY":
            {
                var sync = await _didarApi.SyncAsync(ct);
                var result = await _identities.ReconcileAsync(ct);
                return $"API received={sync.Received}, inserted={sync.Inserted}, updated={sync.Updated}; Contacts={result.TotalActiveDidar}; Phones={result.DidarPhones}; Linked={result.LinkedDidar}.";
            }
            case "ISSABEL_MONITOR":
                await _health.ProbeIssabelAsync(ct);
                return "CDR destination probe completed. Issabel sends CDR incrementally by push.";
            case "MAPPING_FILE":
            {
                var configured = _configuration["DataGathering:MappingFile"] ?? "config/mappingfile.xlsx";
                var path = Path.IsPathRooted(configured) ? configured : Path.Combine(_environment.ContentRootPath, configured);
                if (!File.Exists(path)) throw new FileNotFoundException("Mapping file was not found.", path);
                await using var input = File.OpenRead(path);
                var imported = await _mappings.ImportExcelAsync(input, Path.GetFileName(path), ct);
                var summary = await _mappings.ReconcileAsync(ct);
                return $"Rows={imported.TotalRows}; Linked={summary.LinkedCodes}; Unmapped={summary.UnmappedCodes}; AlreadyImported={imported.AlreadyImported}.";
            }
            case "DATABASE_MAINTENANCE":
            {
                var result = await _maintenance.RunAsync(ct);
                return $"Deleted={result.DeletedOperationalRows}; Recovery={result.RecoveryModel}; LogMb={result.LogSizeMb}; Shrink={result.ShrinkAttempted}.";
            }
            default:
                throw new InvalidOperationException($"No handler is registered for integration job '{jobKey}'.");
        }
    }
}
