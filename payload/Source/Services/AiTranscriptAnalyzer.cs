using DigiAhan.CDR.Receiver.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class AiTranscriptAnalyzer
{
    public const string Version = "rules-fa-steel-v2";

    private static readonly Dictionary<string, string[]> Brands = new()
    {
        ["ZOB_AHAN"] = ["ذوب آهن", "ذوب‌آهن"],
        ["FAICO"] = ["فایکو"],
        ["ARYAN"] = ["آریان"],
        ["YAZD"] = ["یزد"],
        ["ATLAS"] = ["اطلس"]
    };

    private static readonly Dictionary<string, string[]> Topics = new()
    {
        ["PRICE"] = ["قیمت", "نرخ", "گران", "ارزان"],
        ["INVENTORY"] = ["موجود", "موجودی", "نداریم"],
        ["PAYMENT"] = ["پرداخت", "واریز", "حساب", "چک", "نقد"],
        ["DELIVERY"] = ["تحویل", "بارگیری", "ارسال", "بار رو", "بار را"],
        ["TONNAGE"] = ["تناژ", "تن", "کیلو"],
        ["FOLLOW_UP"] = ["پیگیری", "خبر میدم", "خبر می‌دم", "تماس میگیرم", "تماس می‌گیرم"]
    };

    private static readonly Dictionary<string, string[]> NonPurchaseReasons = new()
    {
        ["PRICE_TOO_HIGH"] = ["گرونه", "گران است", "قیمت بالاست", "قیمت زیاد"],
        ["NO_BUDGET_OR_LIQUIDITY"] = ["بودجه ندار", "نقدینگی ندار", "پول ندار"],
        ["OUT_OF_STOCK"] = ["موجود نیست", "موجود ندار", "نداریم"],
        ["DELIVERY_TIME"] = ["دیر میرس", "دیر می‌رس", "زمان تحویل", "تحویل دیر"],
        ["PAYMENT_TERMS"] = ["شرایط پرداخت", "چک قبول", "نقدی نمی"],
        ["COMPETITOR_SELECTED"] = ["از جای دیگه", "از جای دیگر", "رقیب", "خرید کردم"],
        ["NOT_READY"] = ["فعلا نمی", "فعلاً نمی", "بعدا", "بعداً"]
    };

    private static readonly Dictionary<string, string[]> SellerBehaviors = new()
    {
        ["GREETING"] = ["سلام", "وقت بخیر", "در خدمتم"],
        ["CLARIFICATION"] = ["منظورتون", "چه سایزی", "چند تن", "کدام کارخانه", "کدوم کارخانه"],
        ["FOLLOW_UP_COMMITMENT"] = ["پیگیری می‌کنم", "پیگیری میکنم", "خبر میدم", "خبر می‌دم"],
        ["DE_ESCALATION"] = ["حق با شماست", "عذر میخوام", "عذر می‌خوام", "اجازه بدید بررسی"]
    };

    private static readonly Dictionary<string, string[]> RiskSignals = new()
    {
        ["ANGER_OR_ESCALATION"] = ["داد نزن", "عصبانی", "شکایت", "دیگه زنگ نمی", "دیگر زنگ نمی"],
        ["INSULT_OR_PROFANITY"] = ["احمق", "بی‌شعور", "بیشعور", "حروم", "لعنتی"],
        ["BRIBERY_OR_PERSONAL_PAYMENT"] = ["رشوه", "زیرمیزی", "زیر میزی", "سهم من", "کارت شخصی", "حساب شخصی", "پورسانت من"]
    };

    private static readonly Regex NumericToken = new(
        @"(?<!\p{L}|\d)[0-9۰-۹٠-٩]+(?:[.,٬٫][0-9۰-۹٠-٩]+)?(?!\p{L}|\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public AiAnalysisResult Analyze(AiAnalyzeRunRequest request)
    {
        var text = Normalize(request.TranscriptText);
        var segments = ParseSegments(request.SegmentsJson, text);
        var queueRepeats = Count(text, "اولین نفر") + Count(text, "اولین اپراتور") + Count(text, "در صف انتظار");
        var businessHits = Topics.Values.SelectMany(values => values).Count(text.Contains)
            + Brands.Values.SelectMany(values => values).Count(text.Contains);

        var hinted = request.AudioClassHint?.Trim().ToUpperInvariant();
        var audioClass = hinted switch
        {
            "QUEUE_ONLY" => "QUEUE_ONLY",
            "NON_SPEECH_OR_UNSUPPORTED" => "NON_SPEECH_OR_UNSUPPORTED",
            "EMPTY" => "EMPTY",
            "PROCESSING_ERROR" => "PROCESSING_ERROR",
            _ when string.IsNullOrWhiteSpace(text) => "EMPTY",
            _ when queueRepeats >= 2 && businessHits == 0 => "QUEUE_ONLY",
            _ => "BUSINESS_CONVERSATION"
        };

        var hasSpeech = audioClass is not "EMPTY" and not "NON_SPEECH_OR_UNSUPPORTED";
        var business = audioClass == "BUSINESS_CONVERSATION";
        var facts = new List<AiExtractedFact>();
        var reviews = new List<AiReviewItem>();

        if (business)
        {
            AddKeywordFacts(segments, Brands, "BRAND", 0.84m, "AUTO_ACCEPTED", facts);
            AddKeywordFacts(segments, Topics, "TOPIC", 0.80m, "AUTO_ACCEPTED", facts);
            AddKeywordFacts(segments, NonPurchaseReasons, "NON_PURCHASE_REASON", 0.66m, "REVIEW", facts);
            AddKeywordFacts(segments, SellerBehaviors, "BEHAVIOR", 0.58m, "REVIEW", facts);

            foreach (var segment in segments)
            {
                foreach (var risk in RiskSignals)
                {
                    if (!risk.Value.Any(segment.Text.Contains)) continue;
                    var riskConfidence = risk.Key == "BRIBERY_OR_PERSONAL_PAYMENT" ? 0.55m : 0.68m;
                    facts.Add(Fact("RISK_SIGNAL", segment.Text, risk.Key, null,
                        segment.Start, segment.End, riskConfidence, "REVIEW"));
                    reviews.Add(new AiReviewItem(
                        risk.Key,
                        risk.Key == "BRIBERY_OR_PERSONAL_PAYMENT" ? "HIGH" : "MEDIUM",
                        "HUMAN_CONFIRMATION_REQUIRED",
                        segment.Text,
                        segment.Start,
                        segment.End,
                        "OPEN"));
                }
            }

            foreach (var segment in segments)
            {
                foreach (Match match in NumericToken.Matches(segment.Text).Cast<Match>().Take(20))
                {
                    facts.Add(Fact("QUANTITY", match.Value, NormalizeDigits(match.Value), null,
                        segment.Start, segment.End, 0.42m, "REVIEW"));
                }
            }

            foreach (var reason in facts.Where(f => f.FactType == "NON_PURCHASE_REASON"))
            {
                reviews.Add(new AiReviewItem(
                    "NON_PURCHASE_REASON",
                    "MEDIUM",
                    reason.NormalizedValue ?? "UNCLASSIFIED",
                    reason.RawValue,
                    reason.StartSeconds,
                    reason.EndSeconds,
                    "OPEN"));
            }

            foreach (var behavior in facts.Where(f => f.FactType == "BEHAVIOR"))
            {
                reviews.Add(new AiReviewItem(
                    "SELLER_BEHAVIOR",
                    "LOW",
                    "SPEAKER_ATTRIBUTION_REQUIRED",
                    behavior.RawValue,
                    behavior.StartSeconds,
                    behavior.EndSeconds,
                    "OPEN"));
            }

            if (facts.Count == 0)
            {
                reviews.Add(new AiReviewItem(
                    "LOW_INFORMATION", "MEDIUM", "NO_STRUCTURED_FACTS",
                    null, null, null, "OPEN"));
            }
        }

        facts = facts
            .GroupBy(f => new { f.FactType, f.NormalizedValue, f.StartSeconds, f.EndSeconds })
            .Select(group => group.First())
            .Take(300)
            .ToList();
        reviews = reviews
            .GroupBy(r => new { r.Category, r.ReasonCode, r.StartSeconds, r.EndSeconds })
            .Select(group => group.First())
            .Take(100)
            .ToList();

        var summary = BuildSummary(audioClass, facts, reviews);
        var confidence = audioClass switch
        {
            "QUEUE_ONLY" => 0.96m,
            "NON_SPEECH_OR_UNSUPPORTED" => 0.95m,
            "EMPTY" => 1m,
            "PROCESSING_ERROR" => 1m,
            _ => Math.Min(0.9m, 0.55m + businessHits * 0.04m)
        };
        var structured = JsonSerializer.Serialize(new
        {
            schemaVersion = "2.0",
            analyzerVersion = Version,
            audioClass,
            request.Direction,
            request.InternalExtension,
            request.Queue,
            facts,
            reviewItems = reviews
        });

        return new AiAnalysisResult(
            audioClass, hasSpeech, business, confidence,
            summary, facts, reviews, structured);
    }

    private static void AddKeywordFacts(
        IEnumerable<TranscriptSegment> segments,
        IReadOnlyDictionary<string, string[]> rules,
        string factType,
        decimal confidence,
        string reviewStatus,
        ICollection<AiExtractedFact> facts)
    {
        foreach (var segment in segments)
        foreach (var rule in rules)
        {
            var keyword = rule.Value.FirstOrDefault(segment.Text.Contains);
            if (keyword is null) continue;
            facts.Add(Fact(
                factType, segment.Text, rule.Key, null,
                segment.Start, segment.End, confidence, reviewStatus));
        }
    }

    private static List<TranscriptSegment> ParseSegments(string? json, string fallback)
    {
        var result = new List<TranscriptSegment>();
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in document.RootElement.EnumerateArray())
                    {
                        var segmentText = element.TryGetProperty("text", out var textNode)
                            ? Normalize(textNode.GetString())
                            : string.Empty;
                        if (string.IsNullOrWhiteSpace(segmentText)) continue;
                        result.Add(new TranscriptSegment(
                            GetDecimal(element, "start"),
                            GetDecimal(element, "end"),
                            segmentText));
                    }
                }
            }
            catch (JsonException)
            {
                // The API validates JSON. Falling back keeps direct callers safe.
            }
        }
        if (result.Count == 0 && !string.IsNullOrWhiteSpace(fallback))
            result.Add(new TranscriptSegment(null, null, fallback));
        return result;
    }

    private static decimal? GetDecimal(JsonElement element, string name) =>
        element.TryGetProperty(name, out var node) && node.TryGetDecimal(out var value) ? value : null;

    private static AiExtractedFact Fact(
        string type,
        string? raw,
        string? normalized,
        string? unit,
        decimal? start,
        decimal? end,
        decimal confidence,
        string reviewStatus) =>
        new(type, raw, normalized, unit, start, end, confidence, reviewStatus);

    private static string BuildSummary(
        string audioClass,
        IReadOnlyCollection<AiExtractedFact> facts,
        IReadOnlyCollection<AiReviewItem> reviews)
    {
        if (audioClass == "QUEUE_ONLY") return "فقط پیام صف انتظار تشخیص داده شد و مکالمه انسانی وجود ندارد.";
        if (audioClass == "NON_SPEECH_OR_UNSUPPORTED") return "سیگنال صوتی بدون گفتار قابل تحلیل است.";
        if (audioClass == "EMPTY") return "متن یا گفتار قابل استفاده وجود ندارد.";
        if (audioClass == "PROCESSING_ERROR") return "پردازش صوت یا متن ناموفق بود.";

        var topics = facts.Where(f => f.FactType == "TOPIC").Select(f => f.NormalizedValue).Distinct().ToArray();
        var nonPurchase = facts.Count(f => f.FactType == "NON_PURCHASE_REASON");
        var risks = reviews.Count(r => r.Category is "ANGER_OR_ESCALATION" or "INSULT_OR_PROFANITY" or "BRIBERY_OR_PERSONAL_PAYMENT");
        var parts = new List<string> { "مکالمه تجاری" };
        if (topics.Length > 0) parts.Add($"موضوع‌ها: {string.Join("، ", topics)}");
        if (nonPurchase > 0) parts.Add($"دلایل احتمالی عدم خرید: {nonPurchase}");
        if (risks > 0) parts.Add($"موارد حساس نیازمند بررسی انسانی: {risks}");
        return string.Join("؛ ", parts);
    }

    private static string Normalize(string? value) => Regex.Replace(
        (value ?? string.Empty).Replace('ي', 'ی').Replace('ك', 'ک'), @"\s+", " ").Trim();

    private static string NormalizeDigits(string value) => string.Concat(value.Select(character => character switch
    {
        >= '۰' and <= '۹' => (char)('0' + character - '۰'),
        >= '٠' and <= '٩' => (char)('0' + character - '٠'),
        '٬' => ',',
        '٫' => '.',
        _ => character
    }));

    private static int Count(string text, string value) =>
        Regex.Matches(text, Regex.Escape(value)).Count;

    private sealed record TranscriptSegment(decimal? Start, decimal? End, string Text);
}
