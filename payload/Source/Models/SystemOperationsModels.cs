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

public sealed record IntegrationRunNowResult(
    string JobKey,
    string DisplayName,
    bool Started,
    string Status,
    string? Error);

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
    string SellerKey,
    string DisplayName,
    string Extensions,
    int HandledCalls,
    int InboundAnswered,
    int OutboundCalls,
    int AnsweredCalls,
    int Interactions,
    int MissingInteractions,
    int QualityPercent,
    int TalkSeconds,
    int AverageTalkSeconds,
    int FollowUps,
    int Quotes,
    int Orders,
    int Lost);

public sealed record SellerDailyActivityRow(
    DateTime DayUtc,
    string SellerKey,
    string DisplayName,
    string Extensions,
    int UniqueCalls,
    int AnsweredCalls,
    int MissedCalls,
    int UnregisteredResults,
    int Interactions,
    int Quotes,
    int FollowUps,
    int Orders,
    int Lost);

public sealed record SellerActivityRow(
    DateTime EventAtUtc,
    string EventType,
    string SellerKey,
    string SellerDisplayName,
    string? CustomerPhone,
    string? CustomerName,
    string Status,
    string? Outcome,
    string? Details,
    string? LinkedId,
    bool RequiresFollowUp);

public sealed record SellerActivityPage(int Total, IReadOnlyList<SellerActivityRow> Items);
