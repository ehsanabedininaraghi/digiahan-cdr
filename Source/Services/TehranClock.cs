using System.Globalization;

namespace DigiAhan.CDR.Receiver.Services;

public static class TehranClock
{
    private static readonly TimeZoneInfo Zone = ResolveZone();

    public static DateTime UtcNow => DateTime.UtcNow;

    public static DateTime StartOfTodayUtc()
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone);
        return ToUtc(localNow.Date);
    }

    public static DateTime ToUtc(DateTime tehranLocal)
    {
        var unspecified = DateTime.SpecifyKind(tehranLocal, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, Zone);
    }

    public static DateTime AsUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    public static DateTime NormalizeIncomingEventUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DateTime.UtcNow;

        DateTime candidate;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var offset))
        {
            candidate = offset.UtcDateTime;
        }
        else if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                     DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            candidate = parsed.Kind switch
            {
                DateTimeKind.Utc => parsed,
                DateTimeKind.Local => parsed.ToUniversalTime(),
                _ => DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            };
        }
        else
        {
            return DateTime.UtcNow;
        }

        var now = DateTime.UtcNow;
        if (candidate > now.AddMinutes(2))
        {
            var sourceOffset = Zone.GetUtcOffset(now);
            var corrected = candidate.Subtract(sourceOffset);
            if (Math.Abs((corrected - now).TotalMinutes) <= 30)
                candidate = corrected;
        }

        if (candidate > now.AddMinutes(5) || candidate < now.AddDays(-2))
            return now;

        return candidate;
    }

    public static string PersianDateTime(DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc), Zone);
        var calendar = new PersianCalendar();
        return $"{calendar.GetYear(local):0000}/{calendar.GetMonth(local):00}/{calendar.GetDayOfMonth(local):00} {local:HH:mm:ss}";
    }

    private static TimeZoneInfo ResolveZone()
    {
        foreach (var id in new[] { "Iran Standard Time", "Asia/Tehran" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        return TimeZoneInfo.CreateCustomTimeZone("Asia/Tehran", TimeSpan.FromHours(3.5),
            "Asia/Tehran", "Asia/Tehran");
    }
}
