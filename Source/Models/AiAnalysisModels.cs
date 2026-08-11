namespace DigiAhan.CDR.Receiver.Models;

public sealed record AiCallAssessment(
    long RunId,
    string AudioClass,
    bool HasHumanSpeech,
    bool IsBusinessRelevant,
    string Direction,
    string? InternalExtension,
    string? Queue,
    decimal Confidence,
    decimal? SpeechSeconds,
    string? Summary,
    string? StructuredJson,
    string AnalyzerVersion);

public sealed record AiExtractedFact(
    string FactType,
    string? RawValue,
    string? NormalizedValue,
    string? Unit,
    decimal? StartSeconds,
    decimal? EndSeconds,
    decimal Confidence,
    string ReviewStatus);

public sealed record AiReviewItem(
    string Category,
    string Priority,
    string ReasonCode,
    string? RawText,
    decimal? StartSeconds,
    decimal? EndSeconds,
    string Status);
