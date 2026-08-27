namespace DigiAhan.CDR.Receiver.Models;

public sealed record CustomerJourneyOptions
{
    public bool Enabled { get; init; }
    public bool AutoCaptureSellerInteractions { get; init; }
    public int DefaultLeadSlaMinutes { get; init; } = 120;
    public int DefaultFollowUpMinutes { get; init; } = 1_440;
    public string[] PilotSellerKeys { get; init; } = Array.Empty<string>();
}

public sealed record JourneyStats(
    int OpenLeads,
    int ActiveOpportunities,
    int DueToday,
    int Overdue,
    int UnassignedExceptions);

public sealed record JourneyWorkItemRow(
    long WorkItemId,
    long IdentityId,
    long? LeadId,
    long? OpportunityId,
    string WorkType,
    string Title,
    string Status,
    byte Priority,
    DateTime DueAtUtc,
    DateTime SlaDueAtUtc,
    bool IsOverdue,
    string CustomerDisplayName,
    string? PrimaryPhone,
    string? LeadStatus,
    string? OpportunityStage,
    string OwnerSellerKey);

public sealed record JourneyLeadRow(
    long LeadId,
    long IdentityId,
    string CustomerDisplayName,
    string? PrimaryPhone,
    string Title,
    string Status,
    byte Priority,
    string OwnerSellerKey,
    string NextActionType,
    DateTime NextActionAtUtc,
    DateTime SlaDueAtUtc,
    string? ProductSummary,
    DateTime UpdatedAtUtc);

public sealed record JourneyOpportunityRow(
    long OpportunityId,
    long IdentityId,
    long? LeadId,
    string CustomerDisplayName,
    string? PrimaryPhone,
    string Title,
    string Stage,
    string OwnerSellerKey,
    string NextActionType,
    DateTime NextActionAtUtc,
    DateTime SlaDueAtUtc,
    decimal? EstimatedAmount,
    string? ProductSummary,
    DateTime UpdatedAtUtc);

public sealed record JourneyWorkspaceResponse(
    bool Enabled,
    SellerSessionResponse Seller,
    JourneyStats Stats,
    IReadOnlyList<JourneyWorkItemRow> WorkItems,
    IReadOnlyList<JourneyLeadRow> Leads,
    IReadOnlyList<JourneyOpportunityRow> Opportunities,
    DateTime GeneratedAtUtc);

public sealed record JourneyCreateLeadRequest(
    string IdempotencyKey,
    long IdentityId,
    string Title,
    string? ProductSummary,
    byte Priority,
    string NextActionType,
    DateTime NextActionAtUtc,
    string? Note);

public sealed record JourneyLeadCreatedResponse(
    long LeadId,
    long WorkItemId,
    bool AlreadyExisted,
    DateTime CreatedAtUtc);

public sealed record JourneyQualifyLeadRequest(
    string IdempotencyKey,
    string Title,
    string? ProductSummary,
    decimal? Quantity,
    string? QuantityUnit,
    decimal? EstimatedAmount,
    string NextActionType,
    DateTime NextActionAtUtc,
    DateTime? ExpectedCloseAtUtc,
    string? Note);

public sealed record JourneyOpportunityCreatedResponse(
    long OpportunityId,
    long WorkItemId,
    bool AlreadyExisted,
    DateTime CreatedAtUtc);

public sealed record JourneyTransitionOpportunityRequest(
    string IdempotencyKey,
    string Stage,
    string NextActionType,
    DateTime? NextActionAtUtc,
    string? LostReason,
    string? Note);

public sealed record JourneyCompleteWorkItemRequest(
    string IdempotencyKey,
    string Outcome,
    string? Note);

public sealed record JourneyMutationResponse(long Id, string Status, DateTime UpdatedAtUtc);

public sealed record JourneyManagerExceptionRow(
    long WorkItemId,
    long IdentityId,
    string CustomerDisplayName,
    string? PrimaryPhone,
    string OwnerSellerKey,
    string WorkType,
    string Title,
    DateTime DueAtUtc,
    DateTime SlaDueAtUtc,
    int OverdueMinutes,
    string? LeadStatus,
    string? OpportunityStage);

public sealed record JourneyCaptureResult(
    bool Captured,
    long? LeadId,
    long? OpportunityId,
    long? WorkItemId,
    string Reason);

