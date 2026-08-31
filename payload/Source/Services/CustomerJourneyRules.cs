using DigiAhan.CDR.Receiver.Models;

namespace DigiAhan.CDR.Receiver.Services;

public static class CustomerJourneyRules
{
    public static readonly IReadOnlySet<string> LeadStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "OPEN", "QUALIFIED", "DISQUALIFIED", "CONVERTED", "CLOSED" };

    public static readonly IReadOnlySet<string> OpportunityStages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "DISCOVERY", "NEEDS_CONFIRMED", "PRICE_GIVEN", "QUOTE_SENT", "DECISION",
        "NEGOTIATION", "WON", "LOST", "ON_HOLD"
    };

    public static readonly IReadOnlySet<string> WorkItemOutcomes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "DONE", "NO_ANSWER", "RESCHEDULED", "NOT_RELEVANT", "CUSTOMER_DECLINED" };

    public static Guid RequireIdempotencyKey(string? value)
        => Guid.TryParse(value, out var key)
            ? key
            : throw new ArgumentException("IDEMPOTENCY_KEY_INVALID");

    public static DateTime RequireFutureUtc(DateTime value, DateTime nowUtc, string code)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        if (utc <= nowUtc.AddMinutes(-1) || utc > nowUtc.AddYears(2))
            throw new ArgumentException(code);
        return utc;
    }

    public static string RequireText(string? value, int maxLength, string code)
    {
        var clean = value?.Trim();
        if (string.IsNullOrWhiteSpace(clean) || clean.Length > maxLength)
            throw new ArgumentException(code);
        return clean;
    }

    public static string? Clean(string? value, int maxLength)
    {
        var clean = value?.Trim();
        if (string.IsNullOrWhiteSpace(clean)) return null;
        return clean.Length <= maxLength ? clean : clean[..maxLength];
    }

    public static string RequireStage(string? value)
    {
        var stage = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!OpportunityStages.Contains(stage)) throw new ArgumentException("OPPORTUNITY_STAGE_INVALID");
        return stage;
    }

    public static string RequireWorkItemOutcome(string? value)
    {
        var outcome = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!WorkItemOutcomes.Contains(outcome)) throw new ArgumentException("WORK_ITEM_OUTCOME_INVALID");
        return outcome;
    }

    public static byte NormalizePriority(byte priority) => (byte)Math.Clamp((int)priority, 1, 4);

    public static bool IsClosedStage(string stage) => stage is "WON" or "LOST";
}
