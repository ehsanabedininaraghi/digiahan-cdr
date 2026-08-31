using DigiAhan.CDR.Receiver.Models;
using Microsoft.Extensions.Options;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class DailyRecordingIngestionWorker : BackgroundService
{
    private readonly AiPipelineRepository _pipeline;
    private readonly RecordingAssetRepository _assets;
    private readonly IssabelSftpRecordingClient _sftp;
    private readonly RecordingAudioValidator _validator;
    private readonly FasterWhisperTranscriber _transcriber;
    private readonly AiTranscriptAnalyzer _analyzer;
    private readonly AiAnalysisRepository _analysis;
    private readonly IOptionsMonitor<RecordingIngestionOptions> _options;
    private readonly IOptionsMonitor<AiPipelineOptions> _pipelineOptions;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<DailyRecordingIngestionWorker> _logger;
    private readonly string _owner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public DailyRecordingIngestionWorker(
        AiPipelineRepository pipeline,
        RecordingAssetRepository assets,
        IssabelSftpRecordingClient sftp,
        RecordingAudioValidator validator,
        FasterWhisperTranscriber transcriber,
        AiTranscriptAnalyzer analyzer,
        AiAnalysisRepository analysis,
        IOptionsMonitor<RecordingIngestionOptions> options,
        IOptionsMonitor<AiPipelineOptions> pipelineOptions,
        IWebHostEnvironment environment,
        ILogger<DailyRecordingIngestionWorker> logger)
    {
        _pipeline = pipeline;
        _assets = assets;
        _sftp = sftp;
        _validator = validator;
        _transcriber = transcriber;
        _analyzer = analyzer;
        _analysis = analysis;
        _options = options;
        _pipelineOptions = pipelineOptions;
        _environment = environment;
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
                    await RunCycleAsync(options, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Daily recording ingestion cycle failed; the next poll will retry.");
                }
            }

            await Task.Delay(
                TimeSpan.FromSeconds(Math.Clamp(options.PollSeconds, 30, 3600)),
                stoppingToken);
        }
    }

    private async Task RunCycleAsync(RecordingIngestionOptions options, CancellationToken ct)
    {
        ValidateEnabledConfiguration(options);
        var pipeline = _pipelineOptions.CurrentValue;
        await _pipeline.DiscoverAndQueueAsync(
            pipeline.StabilizationSeconds,
            pipeline.DiscoveryBatchSize,
            ct);

        var targetDate = GetTargetDate(options);
        var discovery = await _assets.DiscoverAsync(
            targetDate,
            options.SourceName,
            options.BatchSize,
            ct);
        if (discovery.AssetsDiscovered > 0)
        {
            _logger.LogInformation(
                "Today's recording assets discovered. Date={Date} Assets={Assets} Calls={Calls}",
                targetDate, discovery.AssetsDiscovered, discovery.CallsLinked);
        }

        CleanupAbandonedParts(GetStagingRoot(options), options.LocalRetentionHours);
        for (var processed = 0; processed < Math.Clamp(options.BatchSize, 1, 100); processed++)
        {
            var lease = await _assets.ClaimNextAsync(
                _owner,
                targetDate,
                options.LeaseSeconds,
                options.MaxAttempts,
                ct);
            if (lease is null) break;
            await ProcessLeaseAsync(options, lease, ct);
        }
    }

    private async Task ProcessLeaseAsync(
        RecordingIngestionOptions options,
        RecordingAssetLease lease,
        CancellationToken ct)
    {
        string? finalPath = null;
        try
        {
            finalPath = TryResolveExistingStagedFile(options, lease);
            if (finalPath is null)
                finalPath = await FetchAndValidateAsync(options, lease, ct);
            if (finalPath is null) return;

            await _assets.MarkPhaseAsync(lease.RecordingAssetId, _owner, "TRANSCRIBING", ct);
            var transcript = await _transcriber.TranscribeAsync(options.Transcription, finalPath, ct);
            await _assets.MarkPhaseAsync(lease.RecordingAssetId, _owner, "ANALYZING", ct);
            var request = new AiAnalyzeRunRequest(
                transcript.TranscriptText,
                transcript.SegmentsJson,
                transcript.LanguageCode,
                transcript.AudioDurationSeconds,
                transcript.SpeechSeconds,
                transcript.ProcessingSeconds,
                transcript.Engine,
                transcript.ModelName,
                null,
                null,
                null,
                null);
            var analysis = _analyzer.Analyze(request);
            await _analysis.SaveAnalysisAsync(lease.RunId, request, analysis, ct);
            await _assets.MarkCompletedAsync(
                lease.RecordingAssetId,
                _owner,
                transcript.AudioDurationSeconds,
                ct);

            try
            {
                File.Delete(finalPath);
                DeleteDirectoryIfEmpty(Path.GetDirectoryName(finalPath)!);
                await _assets.MarkPurgedAsync(lease.RecordingAssetId, ct);
            }
            catch (Exception purgeError)
            {
                _logger.LogWarning(
                    purgeError,
                    "Recording analysis completed but local staging purge failed. AssetId={AssetId}",
                    lease.RecordingAssetId);
            }

            _logger.LogInformation(
                "Recording processed and local audio purged. AssetId={AssetId} RunId={RunId} Class={Class}",
                lease.RecordingAssetId, lease.RunId, analysis.AudioClass);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Recording processing failed. AssetId={AssetId} Attempt={Attempt}",
                lease.RecordingAssetId, lease.AttemptCount);
            try
            {
                await _assets.MarkRetryAsync(
                    lease.RecordingAssetId,
                    _owner,
                    lease.AttemptCount,
                    options.MaxAttempts,
                    Limit(ex.Message, 1900),
                    ct);
            }
            catch (Exception statusError)
            {
                _logger.LogError(statusError, "Could not persist recording retry state. AssetId={AssetId}", lease.RecordingAssetId);
            }
        }
    }

    private async Task<string?> FetchAndValidateAsync(
        RecordingIngestionOptions options,
        RecordingAssetLease lease,
        CancellationToken ct)
    {
        var remote = await _sftp.GetInfoAsync(options, lease.OriginalFileName, lease.CallDate, ct);
        if (remote is null)
        {
            await _assets.MarkSourceMissingAsync(
                lease.RecordingAssetId,
                _owner,
                DateTime.UtcNow.AddMinutes(15),
                "Recording was not found below the approved Issabel monitor root.",
                ct);
            return null;
        }
        if (remote.SizeBytes <= 0)
            throw new InvalidDataException("Remote recording size is zero.");

        if (!remote.IsStable)
        {
            await _assets.MarkWaitingAsync(
                lease.RecordingAssetId,
                _owner,
                remote,
                DateTime.UtcNow.AddSeconds(Math.Clamp(options.StabilitySeconds, 60, 3600)),
                ct);
            return null;
        }

        var paths = BuildStagingPaths(options, lease);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.FinalPath)!);
        if (File.Exists(paths.FinalPath))
        {
            var existing = await _validator.ValidateWavAsync(paths.FinalPath, remote.SizeBytes, ct);
            await _assets.MarkFetchingAsync(lease.RecordingAssetId, _owner, remote, paths.StorageKey, ct);
            await _assets.MarkReadyForAiAsync(lease.RecordingAssetId, _owner, existing, ct);
            return paths.FinalPath;
        }

        await _assets.MarkFetchingAsync(lease.RecordingAssetId, _owner, remote, paths.StorageKey, ct);
        var partPath = paths.FinalPath + $".part-{Guid.NewGuid():N}";
        try
        {
            await _sftp.DownloadAsync(options, remote.FullPath, partPath, ct);
            var validated = await _validator.ValidateWavAsync(partPath, remote.SizeBytes, ct);
            File.Move(partPath, paths.FinalPath);
            await _assets.MarkReadyForAiAsync(lease.RecordingAssetId, _owner, validated, ct);
            return paths.FinalPath;
        }
        finally
        {
            if (File.Exists(partPath)) File.Delete(partPath);
        }
    }

    private string? TryResolveExistingStagedFile(
        RecordingIngestionOptions options,
        RecordingAssetLease lease)
    {
        if (lease.ProcessingStatus is not ("READY_FOR_AI" or "TRANSCRIBING" or "ANALYZING"))
            return null;
        if (string.IsNullOrWhiteSpace(lease.StorageKey)) return null;
        var root = GetStagingRoot(options);
        var full = Path.GetFullPath(Path.Combine(root, lease.StorageKey.Replace('/', Path.DirectorySeparatorChar)));
        EnsureInsideRoot(root, full);
        return File.Exists(full) ? full : null;
    }

    private (string StorageKey, string FinalPath) BuildStagingPaths(
        RecordingIngestionOptions options,
        RecordingAssetLease lease)
    {
        var fileName = Path.GetFileName(lease.OriginalFileName.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException("Recording filename is invalid.");
        var gregorianDate = lease.CallDate.ToString(
            "yyyy/MM/dd",
            System.Globalization.CultureInfo.InvariantCulture);
        var storageKey = $"{gregorianDate}/{lease.RecordingAssetId}/{fileName}";
        var root = GetStagingRoot(options);
        var full = Path.GetFullPath(Path.Combine(root, storageKey.Replace('/', Path.DirectorySeparatorChar)));
        EnsureInsideRoot(root, full);
        return (storageKey, full);
    }

    private string GetStagingRoot(RecordingIngestionOptions options) => Path.GetFullPath(
        Path.IsPathRooted(options.StagingRoot)
            ? options.StagingRoot
            : Path.Combine(_environment.ContentRootPath, options.StagingRoot));

    private static DateOnly GetTargetDate(RecordingIngestionOptions options)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
        return DateOnly.FromDateTime(localNow.AddDays(Math.Clamp(options.TargetDateOffsetDays, -1, 0)));
    }

    private static void ValidateEnabledConfiguration(RecordingIngestionOptions options)
    {
        if (options.TargetDateOffsetDays is < -1 or > 0)
            throw new InvalidOperationException("TargetDateOffsetDays is limited to today (0) or yesterday (-1); historical backfill is disabled.");
        if (string.IsNullOrWhiteSpace(options.SourceName))
            throw new InvalidOperationException("RecordingIngestion:SourceName is required.");
    }

    private static void EnsureInsideRoot(string root, string path)
    {
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Resolved staging path escapes the configured root.");
    }

    private static void CleanupAbandonedParts(string root, int retentionHours)
    {
        if (!Directory.Exists(root)) return;
        var cutoff = DateTime.UtcNow.AddHours(-Math.Clamp(retentionHours, 1, 168));
        foreach (var file in Directory.EnumerateFiles(root, "*.part-*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (info.LastWriteTimeUtc < cutoff) info.Delete();
        }
    }

    private static void DeleteDirectoryIfEmpty(string directory)
    {
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }

    private static string Limit(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];
}
