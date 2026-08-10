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
    int Overdue);

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
    DateTime GeneratedAtUtc);

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
    public string AccessToken { get; init; } = string.Empty;
    public string[] Extensions { get; init; } = Array.Empty<string>();
    public string[] ProductGroups { get; init; } = Array.Empty<string>();
}

public sealed record SellerWorkspaceOptions
{
    public bool Enabled { get; init; }
    public SellerWorkspaceAgentOptions[] Agents { get; init; } = Array.Empty<SellerWorkspaceAgentOptions>();
}
