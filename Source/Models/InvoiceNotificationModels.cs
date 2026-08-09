namespace DigiAhan.CDR.Receiver.Models;

public sealed record InvoiceNotificationListItem(
    long Id,
    string? InvoiceNumber,
    string? FactorDate,
    string? CustomerName,
    string? ProductSummary,
    string DeliveryVoucherNumber,
    string? PrimaryPhone,
    IReadOnlyList<string> AvailablePhones,
    string Status,
    DateTime CreatedAtUtc,
    DateTime? PreparedAtUtc,
    DateTime? SmsSentAt,
    string? PreparedBy,
    string? SentBy);

public sealed record InvoiceNotificationDiscoveryResult(
    int Scanned,
    int Ready,
    int NeedsIdentity,
    int NeedsPhone,
    DateTime FinishedAtUtc);

public sealed record PrepareInvoiceNotificationsRequest(IReadOnlyList<long>? NotificationIds, string? Actor);

public sealed record PreparedInvoiceNotification(
    long NotificationId,
    string Phone,
    string SmsText);

public sealed record SetPrimaryMobileRequest(string? Phone, string? Actor);

public sealed record MarkManualSentRequest(string? Actor, string? Note);

public sealed record PublicOrderProduct(string Product, double? Quantity);

public sealed record PublicOrderView(
    string DeliveryVoucherNumber,
    string? PurchaseDate,
    string? ProductSummary,
    IReadOnlyList<PublicOrderProduct> Products);
