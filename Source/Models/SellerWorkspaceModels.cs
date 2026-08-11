namespace DigiAhan.CDR.Receiver.Models;

public sealed record SellerIdentity(
    string Key,
    string DisplayName,
    string[] Extensions,
    string[] ProductGroups);

public sealed record SellerSessionResponse(
    string Key,
    string DisplayName,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> ProductGroups);

public sealed record SellerTodayStats(
    int Conversations,
    int Priced,
    int Orders,
    int Lost,
    int MissingResults,
    int DueToday,
    int Overdue,
    int QualityPercent);

public sealed record SellerCustomerSearchRow(
    long IdentityId,
    string Phone,
    string DisplayName,
    string? CompanyName,
    string? MobilePhones,
    string? OwnerName,
    string Source);

public sealed record SellerCustomerProfile(
    long? IdentityId,
    string? DisplayName,
    string? CompanyName,
    string? OwnerName,
    IReadOnlyList<string> Phones,
    bool IsActive,
    string Source);

public sealed record SellerCustomerSaveRequest(
    string? DisplayName,
    string? CompanyName,
    string? OwnerName,
    IReadOnlyList<string>? Phones);

public sealed record SellerCustomerSaveResult(
    long IdentityId,
    string PrimaryPhone,
    bool Created,
    DateTime SavedAtUtc);

public sealed record SellerMissingResultRow(
    long Id,
    string CustomerPhone,
    string CustomerDisplayName,
    DateTime EventAtUtc,
    string? LinkedId);

public sealed record SellerCurrentCallResponse(
    AgentCustomerCard Card,
    DateTime PublishedAtUtc,
    DateTime ServerNowUtc);

public sealed record SellerFollowUpRow(
    long Id,
    string CustomerPhone,
    string CustomerDisplayName,
    string Subject,
    DateTime DueAtUtc,
    string Status,
    long InteractionId);

public sealed record SellerTimelineRow(
    long Id,
    string EventType,
    DateTime EventAtUtc,
    string SellerDisplayName,
    string Title,
    string? Description,
    string? ProductName,
    string? ProductSize,
    decimal? Quantity,
    string? QuantityUnit,
    string? Outcome,
    string? LossReason,
    bool IsMine);

public sealed record SellerWorkspaceResponse(
    SellerSessionResponse Seller,
    AgentCustomerCard? Customer,
    SellerTodayStats Stats,
    IReadOnlyList<SellerFollowUpRow> FollowUps,
    IReadOnlyList<SellerTimelineRow> Timeline,
    bool ReadOnlyCustomer,
    DateTime GeneratedAtUtc);

public sealed record SellerLoginRequest(string Username, string Password);

public sealed record SellerAdminUserRow(
    long Id,
    string Username,
    string SellerKey,
    string DisplayName,
    bool IsActive,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> ProductGroups,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? LastLoginAtUtc,
    int ActiveSessions);

public sealed record SellerAdminUserSaveRequest(
    string Username,
    string SellerKey,
    string DisplayName,
    string? Password,
    bool IsActive,
    IReadOnlyList<string>? Extensions,
    IReadOnlyList<string>? ProductGroups);

public sealed record SellerAdminPasswordResetRequest(string NewPassword);

public sealed record SellerInteractionRequest(
    string IdempotencyKey,
    string CustomerPhone,
    string? CallLinkedId,
    DateTime? OccurredAtUtc,
    string? ProductName,
    string? ProductSize,
    string? ProductBrand,
    decimal? Quantity,
    string? QuantityUnit,
    IReadOnlyList<string>? Actions,
    string Outcome,
    string? LossReason,
    string? CompetitorName,
    decimal? CompetitorPrice,
    DateTime? FollowUpAtUtc,
    string? FollowUpSubject,
    string? Note);

public sealed record SellerInteractionResult(
    long Id,
    string IdempotencyKey,
    bool AlreadyExisted,
    DateTime CreatedAtUtc);

public sealed record SellerFollowUpCompleteRequest(string IdempotencyKey);

public sealed record SellerWorkspaceAgentOptions
{
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string InitialPassword { get; init; } = string.Empty;
    public string AccessToken { get; init; } = string.Empty;
    public string[] Extensions { get; init; } = Array.Empty<string>();
    public string[] ProductGroups { get; init; } = Array.Empty<string>();
}

public sealed record SellerWorkspaceOptions
{
    public bool Enabled { get; init; }
    public SellerWorkspaceAgentOptions[] Agents { get; init; } = Array.Empty<SellerWorkspaceAgentOptions>();
}
