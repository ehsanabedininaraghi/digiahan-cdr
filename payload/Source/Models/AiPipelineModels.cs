namespace DigiAhan.CDR.Receiver.Models;

public sealed record AiDiscoveryResult(
    int CallsDiscovered,
    int CallsFinalized,
    int RunsQueued,
    DateTime ExecutedAtUtc);

public sealed record AiPipelineRunLease(
    long RunId,
    long LogicalCallId,
    int RunNumber,
    string CallKey,
    string RecordingFile,
    long SourceMaxRawCdrId,
    DateTime LeaseExpiresAtUtc);
