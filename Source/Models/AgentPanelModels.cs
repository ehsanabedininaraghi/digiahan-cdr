namespace DigiAhan.CDR.Receiver.Models;

public sealed record AgentOutcomeRequest(
    string Extension,
    string CallerNumber,
    string Outcome,
    string? Note,
    DateTime? FollowUpAt,
    string? LinkedId);

public sealed record AgentOutcomeRow(
    long Id,
    string Extension,
    string CallerNumber,
    string Outcome,
    string? Note,
    DateTime? FollowUpAt,
    string? LinkedId,
    DateTime CreatedAtUtc);

public sealed record AgentIncomingEventRow(
    long Id,
    string Extension,
    string CallerNumber,
    string? LinkedId,
    DateTime EventTimeUtc,
    string? CustomerName,
    string? CompanyName,
    string? OwnerName,
    bool IsKnownCustomer,
    string CustomerRank,
    string Temperature,
    string? LastInvoiceDate,
    decimal? LastInvoiceAmount,
    string? LastProduct,
    decimal Sales30Days,
    DateTime CreatedAtUtc);

public sealed record AgentPanelStats(
    int CallsToday,
    int OutcomesToday,
    int FollowUpsToday,
    int QuotesToday,
    int OrdersToday,
    int PendingFollowUps);
