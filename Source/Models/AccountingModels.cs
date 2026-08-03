namespace DigiAhan.CDR.Receiver.Models;

public sealed record AccountingSyncResult(
    Guid RunId,
    DateTime StartedAtUtc,
    DateTime FinishedAtUtc,
    string SourceServer,
    string SourceDatabase,
    int FiscalYear,
    string CutoffPersianDate,
    int Visitors,
    int Customers,
    int Invoices,
    int InvoiceItems,
    string Status,
    string? Error);

public sealed record AccountingSyncStatus(
    bool Configured,
    DateTime? LastStartedAtUtc,
    DateTime? LastFinishedAtUtc,
    string? LastStatus,
    int LastCustomers,
    int LastInvoices,
    int LastInvoiceItems,
    string? SourceServer,
    string? SourceDatabase,
    int? FiscalYear,
    string? LastError);
