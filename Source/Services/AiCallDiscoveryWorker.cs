using Microsoft.Extensions.Options;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class AiPipelineOptions
{
    public bool Enabled { get; set; }
    public int PollSeconds { get; set; } = 60;
    public int StabilizationSeconds { get; set; } = 300;
    public int DiscoveryBatchSize { get; set; } = 500;
}

public sealed class AiCallDiscoveryWorker : BackgroundService
{
    private readonly AiPipelineRepository _repository;
    private readonly IOptionsMonitor<AiPipelineOptions> _options;
    private readonly ILogger<AiCallDiscoveryWorker> _logger;

    public AiCallDiscoveryWorker(
        AiPipelineRepository repository,
        IOptionsMonitor<AiPipelineOptions> options,
        ILogger<AiCallDiscoveryWorker> logger)
    {
        _repository = repository;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _options.CurrentValue;
            if (options.Enabled)
            {
                try
                {
                    var result = await _repository.DiscoverAndQueueAsync(
                        options.StabilizationSeconds,
                        options.DiscoveryBatchSize,
                        stoppingToken);
                    if (result.CallsDiscovered > 0 || result.RunsQueued > 0)
                    {
                        _logger.LogInformation(
                            "AI call discovery completed. Discovered={Discovered} Finalized={Finalized} Queued={Queued}",
                            result.CallsDiscovered,
                            result.CallsFinalized,
                            result.RunsQueued);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "AI call discovery failed; the next poll will retry.");
                }
            }

            var delay = TimeSpan.FromSeconds(Math.Clamp(options.PollSeconds, 15, 3600));
            await Task.Delay(delay, stoppingToken);
        }
    }
}
