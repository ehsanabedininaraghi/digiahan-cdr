namespace DigiAhan.CDR.Receiver.Models;

public sealed record IntegrationScheduleRow(
    string JobKey,
    string DisplayName,
    int IntervalMinutes,
    bool IsEnabled,
    DateTime? LastStartedAtUtc,
    DateTime? LastFinishedAtUtc,
    string? LastStatus,
    long? LastDurationMs,
    string? LastError,
    DateTime NextRunAtUtc,
    int ConsecutiveFailures);

public sealed record IntegrationScheduleUpdate(int IntervalMinutes, bool IsEnabled);

public sealed record SystemHealthSnapshot(
    string SqlStatus,
    string DidarStatus,
    long DidarContacts,
    long DidarPhones,
    DateTime? LastDidarSourceSyncAtUtc,
    string IssabelStatus,
    DateTime? LastCdrAt,
    string AccountingStatus,
    DateTime? LastAccountingSyncAtUtc,
    string? LastAccountingFactorDate,
    string RecoveryModel,
    decimal DatabaseSizeMb,
    decimal LogSizeMb,
    int PendingJobs,
    IReadOnlyList<IntegrationScheduleRow> Jobs,
    DateTime GeneratedAtUtc);

public sealed record SellerPerformanceRow(
    string Extension,
    int FollowUps,
    int Quotes,
    int Orders,
    int NoNeed,
    int Notes,
    int TotalOutcomes);
