namespace DigiAhan.CDR.Receiver.Models;

public sealed class RecordingIngestionOptions
{
    public bool Enabled { get; set; }
    public string SourceName { get; set; } = "issabel-primary";
    public string Host { get; set; } = "192.168.8.2";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public string PrivateKeyPath { get; set; } = string.Empty;
    public string KnownHostsPath { get; set; } = string.Empty;
    public string SftpExecutable { get; set; } = "sftp.exe";
    public string RemoteRoot { get; set; } = "/var/spool/asterisk/monitor";
    public string StagingRoot { get; set; } = "Recordings/Staging";
    public int PollSeconds { get; set; } = 300;
    public int StabilitySeconds { get; set; } = 180;
    public int BatchSize { get; set; } = 20;
    public int LeaseSeconds { get; set; } = 1800;
    public int MaxAttempts { get; set; } = 5;
    public int LocalRetentionHours { get; set; } = 24;
    public string TimeZoneId { get; set; } = "Iran Standard Time";
    public int TargetDateOffsetDays { get; set; }
    public RecordingTranscriptionOptions Transcription { get; set; } = new();
}

public sealed class RecordingTranscriptionOptions
{
    public string PythonExecutable { get; set; } = "python";
    public string PythonPath { get; set; } = string.Empty;
    public string ScriptPath { get; set; } = "tools/ai/transcribe_sample.py";
    public string ModelName { get; set; } = "small";
    public string ModelCache { get; set; } = "Models/Whisper";
    public int Threads { get; set; } = 2;
    public int TimeoutMinutes { get; set; } = 60;
    public string? InitialPrompt { get; set; }
    public string? Hotwords { get; set; }
}

public sealed record RecordingAssetLease(
    long RecordingAssetId,
    long LogicalCallId,
    long RunId,
    string SourceServer,
    string OriginalFileName,
    string? SourceRelativePath,
    string? StorageKey,
    string ProcessingStatus,
    int AttemptCount,
    DateTime CallDate,
    DateTime LeaseExpiresAtUtc);

public sealed record RecordingAssetView(
    long RecordingAssetId,
    string OriginalFileName,
    string ProcessingStatus,
    long? FileSizeBytes,
    DateTime? CompletedAtUtc,
    DateTime? PurgedAtUtc,
    string? LastError);

public sealed record RecordingDiscoveryResult(
    int AssetsDiscovered,
    int CallsLinked,
    DateTime ExecutedAtUtc);

public sealed record RemoteRecordingInfo(
    string RelativePath,
    string FullPath,
    long SizeBytes,
    bool IsStable,
    DateTime ObservedAtUtc);

public sealed record ValidatedRecording(
    long SizeBytes,
    string Sha256);

public sealed record TranscriptionResult(
    string TranscriptText,
    string SegmentsJson,
    string LanguageCode,
    decimal AudioDurationSeconds,
    decimal SpeechSeconds,
    decimal ProcessingSeconds,
    string Engine,
    string ModelName);
