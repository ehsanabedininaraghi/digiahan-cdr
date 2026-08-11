namespace DigiAhan.CDR.Receiver.Models;

public sealed record CustomerMappingInputRow(
    int RowNumber,
    string RawAccountingCode,
    string? CustomerName,
    string? RawPhone);

public sealed record CustomerMappingImportResult(
    Guid ImportId,
    string FileName,
    int TotalRows,
    int LinkedRows,
    int UnmappedRows,
    int ConflictRows,
    int InvalidRows,
    bool AlreadyImported);

public sealed record CustomerMappingSummary(
    int TotalCodes,
    int LinkedCodes,
    int UnmappedCodes,
    int ConflictCodes,
    int InvalidCodes,
    DateTime? LastImportedAtUtc,
    string? LastFileName);

public sealed record UnmappedAccountingCode(
    string AccountingCode,
    string? CustomerName,
    string Status,
    string? ErrorMessage,
    DateTime UpdatedAtUtc);

public sealed record DataGatheringRunResult(
    Guid RunId,
    DateTime StartedAtUtc,
    DateTime FinishedAtUtc,
    string Status,
    string AccountingStatus,
    int LinkedCodes,
    int UnmappedCodes,
    string? Error);

public sealed record DataGatheringStatus(
    bool Enabled,
    int IntervalMinutes,
    DateTime? LastStartedAtUtc,
    DateTime? LastFinishedAtUtc,
    string? LastStatus,
    string? LastAccountingStatus,
    int LastLinkedCodes,
    int LastUnmappedCodes,
    string? LastError,
    int ProgressPercent = 0,
    string? ProgressStage = null,
    bool IsRunning = false);
