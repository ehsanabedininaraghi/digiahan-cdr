namespace DigiAhan.CDR.Receiver.Services;

public sealed class DataGatheringWorker : BackgroundService
{
    private readonly DataGatheringCoordinator _coordinator;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DataGatheringWorker> _logger;
    private DateTime _lastUnmappedAlertUtc = DateTime.MinValue;

    public DataGatheringWorker(
        DataGatheringCoordinator coordinator,
        IConfiguration configuration,
        ILogger<DataGatheringWorker> logger)
    {
        _coordinator = coordinator;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_coordinator.Enabled)
        {
            _logger.LogInformation("Data gathering scheduler is disabled.");
            return;
        }

        if (_configuration.GetValue("DataGathering:RunOnStartup", true))
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                await RunAndAlertAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_coordinator.IntervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunAndAlertAsync(stoppingToken);
    }

    private async Task RunAndAlertAsync(CancellationToken ct)
    {
        var result = await _coordinator.RunAsync(ct);
        _logger.LogInformation(
            "Data gathering completed. RunId={RunId} Status={Status} Accounting={Accounting} Linked={Linked} Unmapped={Unmapped}",
            result.RunId, result.Status, result.AccountingStatus, result.LinkedCodes, result.UnmappedCodes);

        var alertHours = Math.Clamp(_configuration.GetValue("DataGathering:UnmappedAlertHours", 24), 1, 168);
        if (result.UnmappedCodes > 0 && DateTime.UtcNow - _lastUnmappedAlertUtc >= TimeSpan.FromHours(alertHours))
        {
            _lastUnmappedAlertUtc = DateTime.UtcNow;
            _logger.LogWarning(
                "UNMAPPED ACCOUNTING ALERT: {Count} accounting codes need a telephone/customer mapping. See GET /api/mappings/unmapped.",
                result.UnmappedCodes);
        }
    }
}
