namespace DigiAhan.CDR.Receiver.Models;

public sealed record VoipRingEventRequest(
    string Extension,
    string CallerNumber,
    string? LinkedId,
    string? Channel,
    DateTime? EventTimeUtc);

public sealed record AgentCustomerCard(
    string Extension,
    string CallerNumber,
    DateTime EventTimeUtc,
    string? LinkedId,
    string? CustomerName,
    string? CompanyName,
    string? OwnerName,
    string? DidarContactCode,
    bool IsKnownCustomer,
    DateTime? LastCallAt,
    int CallsLast30Days,
    string? AccountingCustomerCode,
    string? AccountingCustomerName,
    string? LastInvoiceDate,
    decimal? LastInvoiceAmount,
    string? LastProduct,
    int InvoiceCount30Days,
    decimal Sales30Days,
    string CustomerRank,
    string Temperature,
    string SuggestedOpening,
    int? LastInvoiceDaysAgo,
    string IdentitySource,
    string CustomerRankReason,
    decimal? AccountBalance,
    decimal? CreditLimit);

public sealed record AgentEventEnvelope(
    long Sequence,
    DateTime PublishedAtUtc,
    AgentCustomerCard Card);
