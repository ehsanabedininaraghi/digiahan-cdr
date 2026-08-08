namespace DigiAhan.CDR.Receiver.Models;

public sealed record DashboardSummary(
    DateTime Date,
    int TotalCalls,
    int AnsweredCalls,
    int MissedCalls,
    int InboundCalls,
    int OutboundCalls,
    int TotalTalkSeconds,
    int AverageTalkSeconds,
    int KnownCustomerCalls,
    int NewCustomerCalls,
    DateTime? LastCallAt,
    DateTime? LastReceivedAtUtc);

public sealed record HourlyPoint(int Hour, int Total, int Answered, int Missed);

public sealed record DailyPoint(
    DateTime Date,
    int Total,
    int Answered,
    int Missed,
    int Inbound,
    int Outbound,
    int NewCustomers,
    int KnownCustomers,
    int TalkSeconds);

public sealed record ExtensionStat(
    string Extension,
    int Total,
    int Inbound,
    int Outbound,
    int Answered,
    int Missed,
    int TalkSeconds,
    int AverageTalkSeconds);

public sealed record CallRow(
    long Id,
    DateTime? Calldate,
    string? Src,
    string? Dst,
    string Direction,
    string? Disposition,
    int Duration,
    int Billsec,
    string? RecordingFile,
    string? LinkedId,
    string? UniqueId,
    string? Did,
    string? Dcontext,
    string? CustomerPhone,
    string? CustomerName,
    string? CompanyName,
    string? OwnerName,
    string? DidarContactCode,
    bool IsNewCustomer);

public sealed record CallsPage(int Total, int Page, int PageSize, IReadOnlyList<CallRow> Items);

public sealed record SyncStatus(
    DateTime? LastBatchStartedAtUtc,
    DateTime? LastBatchFinishedAtUtc,
    string? LastBatchStatus,
    int LastBatchInserted,
    int LastBatchDuplicates,
    int LastBatchErrors,
    DateTime? LastReceivedAtUtc,
    DateTime? LastCdrAt,
    int RowsLastHour);
