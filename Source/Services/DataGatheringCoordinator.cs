using DigiAhan.CDR.Receiver.Models;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class DataGatheringCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LegacyAccountingBridgeRunner _accountingBridge;
    private readonly CustomerIdentityReconcileService _identities;
    private readonly CustomerMappingService _mappings;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<DataGatheringCoordinator> _logger;

    public DataGatheringCoordinator(
        LegacyAccountingBridgeRunner accountingBridge,
        CustomerIdentityReconcileService identities,
        CustomerMappingService mappings,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<DataGatheringCoordinator> logger)
    {
        _accountingBridge = accountingBridge;
        _identities = identities;
        _mappings = mappings;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    public bool Enabled => _configuration.GetValue("DataGathering:Enabled", true);
    public int IntervalMinutes => Math.Clamp(_configuration.GetValue("DataGathering:IntervalMinutes", 15), 5, 1440);

    public async Task<DataGatheringRunResult> RunAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            var now = DateTime.UtcNow;
            return new DataGatheringRunResult(Guid.Empty, now, now, "SKIPPED", "BUSY", 0, 0,
                "Another data gathering run is already active.");
        }

        var runId = Guid.NewGuid();
        var started = DateTime.UtcNow;
        try
        {
            await _mappings.StartRunAsync(runId, started, ct);
            var accountingStatus = "NOT_CONFIGURED";
            if (_accountingBridge.IsConfigured)
            {
                var days = Math.Clamp(_configuration.GetValue("DataGathering:AccountingDays", 60), 1, 365);
                var accounting = await _accountingBridge.RunAsync(days, ct);
                accountingStatus = accounting.Status;
            }

            if (accountingStatus != "FAILED")
                await _identities.ReconcileAsync(ct);

            var mappingPath = _configuration["DataGathering:MappingFile"];
            if (!string.IsNullOrWhiteSpace(mappingPath))
            {
                var resolved = Path.IsPathRooted(mappingPath)
                    ? mappingPath
                    : Path.Combine(_environment.ContentRootPath, mappingPath);
                if (File.Exists(resolved))
                {
                    await using var input = File.OpenRead(resolved);
                    await _mappings.ImportExcelAsync(input, Path.GetFileName(resolved), ct);
                }
            }

            var summary = await _mappings.ReconcileAsync(ct);
            var status = accountingStatus == "FAILED" ? "PARTIAL" : "SUCCESS";
            var result = new DataGatheringRunResult(runId, started, DateTime.UtcNow, status,
                accountingStatus, summary.LinkedCodes, summary.UnmappedCodes + summary.ConflictCodes, null);
            await _mappings.FinishRunAsync(result, ct);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Data gathering failed. RunId={RunId}", runId);
            var result = new DataGatheringRunResult(runId, started, DateTime.UtcNow, "FAILED", "FAILED", 0, 0, ex.Message);
            try { await _mappings.FinishRunAsync(result, CancellationToken.None); } catch { }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<DataGatheringStatus> GetStatusAsync(CancellationToken ct) =>
        _mappings.GetGatheringStatusAsync(Enabled, IntervalMinutes, ct);
}
