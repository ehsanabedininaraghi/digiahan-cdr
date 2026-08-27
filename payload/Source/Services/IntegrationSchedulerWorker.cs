namespace DigiAhan.CDR.Receiver.Services;

public sealed class IntegrationSchedulerWorker : BackgroundService
{
    private readonly IntegrationSchedulerService _scheduler;
    private readonly ILogger<IntegrationSchedulerWorker> _logger;

    public IntegrationSchedulerWorker(IntegrationSchedulerService scheduler, ILogger<IntegrationSchedulerWorker> logger)
    {
        _scheduler = scheduler;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        do
        {
            try { await _scheduler.RunDueAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { _logger.LogError(ex, "Integration scheduler cycle failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
