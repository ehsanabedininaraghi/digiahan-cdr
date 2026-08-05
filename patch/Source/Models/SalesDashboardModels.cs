namespace DigiAhan.CDR.Receiver.Models;

public sealed record SalesDashboardSummary(
    bool Connected,
    DateTime? LastSyncAtUtc,
    string? LastSyncStatus,
    int InvoiceCount,
    int CustomerCount,
    decimal TotalSales,
    decimal AverageInvoice,
    int VisitorCount,
    string SourceDatabase,
    int FiscalYear);

public sealed record SalesByVisitorRow(
    int VisitorId,
    string VisitorName,
    string RoleType,
    bool IsActive,
    int InvoiceCount,
    decimal TotalSales,
    decimal AverageInvoice);

public sealed record RecentInvoiceRow(
    int FactorCode,
    decimal? FactorNumber,
    string? FactorDate,
    string? CustomerDetailCode,
    string? CustomerName,
    decimal Amount,
    int? VisitorId,
    string? VisitorName,
    int ItemCount);
