namespace DigiAhan.CDR.Receiver.Models;

public sealed record AiAnalyzeRunRequest(
    string TranscriptText,
    string? SegmentsJson,
    string? LanguageCode,
    decimal? AudioDurationSeconds,
    decimal? SpeechSeconds,
    decimal? ProcessingSeconds,
    string? Engine,
    string? ModelName,
    string? Direction,
    string? InternalExtension,
    string? Queue,
    string? AudioClassHint);

public sealed record AiAnalysisResult(
    string AudioClass,
    bool HasHumanSpeech,
    bool IsBusinessRelevant,
    decimal Confidence,
    string Summary,
    IReadOnlyList<AiExtractedFact> Facts,
    IReadOnlyList<AiReviewItem> ReviewItems,
    string StructuredJson);

public sealed record AiCallListItem(
    long LogicalCallId,
    long RunId,
    string CallKey,
    DateTime? StartedAt,
    int LegCount,
    string? RecordingFile,
    string RunStatus,
    string? AudioClass,
    string? Direction,
    string? InternalExtension,
    decimal? Confidence,
    string? Summary,
    int FactCount,
    int OpenReviewCount);

public sealed record AiCallDetail(
    AiCallListItem Call,
    string? TranscriptText,
    string? SegmentsJson,
    RecordingAssetView? Recording,
    IReadOnlyList<AiFactView> Facts,
    IReadOnlyList<AiReviewView> ReviewItems);

public sealed record AiFactView(
    long FactId,
    string FactType,
    string? RawValue,
    string? NormalizedValue,
    string? Unit,
    decimal? StartSeconds,
    decimal? EndSeconds,
    decimal Confidence,
    string ReviewStatus);

public sealed record AiReviewView(
    long ReviewItemId,
    long LogicalCallId,
    string Category,
    string Priority,
    string ReasonCode,
    string? RawText,
    decimal? StartSeconds,
    decimal? EndSeconds,
    string ReviewStatus,
    string? Resolution,
    DateTime CreatedAtUtc);

public sealed record AiReviewResolutionRequest(
    string Status,
    string? Resolution,
    string? ResolvedBy);
