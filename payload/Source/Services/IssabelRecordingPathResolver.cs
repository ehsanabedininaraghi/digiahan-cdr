using System.Text.RegularExpressions;

namespace DigiAhan.CDR.Receiver.Services;

public sealed partial class IssabelRecordingPathResolver
{
    [GeneratedRegex(@"(?<!\d)(20\d{6})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex DateTokenRegex();

    public string ResolveRelativePath(string recordingFile, DateTime? callDate = null)
    {
        if (string.IsNullOrWhiteSpace(recordingFile))
            throw new ArgumentException("Recording file is required.", nameof(recordingFile));

        var candidate = recordingFile.Trim().Replace('\\', '/');
        const string standardRoot = "/var/spool/asterisk/monitor/";
        if (candidate.StartsWith(standardRoot, StringComparison.Ordinal))
            candidate = candidate[standardRoot.Length..];
        else if (candidate.StartsWith('/'))
            throw new InvalidOperationException("Absolute recording path is outside the approved Issabel monitor root.");

        var segments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(IsUnsafeSegment))
            throw new InvalidOperationException("Recording path contains an unsafe segment.");

        if (segments.Length > 1)
            return string.Join('/', segments);

        var fileName = segments[0];
        var match = DateTokenRegex().Match(fileName);
        DateTime date;
        if (match.Success && DateTime.TryParseExact(
                match.Groups[1].Value,
                "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var parsed))
        {
            date = parsed;
        }
        else if (callDate.HasValue)
        {
            date = callDate.Value;
        }
        else
        {
            throw new InvalidOperationException("Recording filename has no YYYYMMDD token and no call date was supplied.");
        }

        return $"{date.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture)}/{fileName}";
    }

    public string ResolveRemotePath(string remoteRoot, string recordingFile, DateTime? callDate = null)
    {
        if (string.IsNullOrWhiteSpace(remoteRoot) || !remoteRoot.StartsWith('/'))
            throw new InvalidOperationException("RemoteRoot must be an absolute Linux path.");
        return $"{remoteRoot.TrimEnd('/')}/{ResolveRelativePath(recordingFile, callDate)}";
    }

    private static bool IsUnsafeSegment(string segment) =>
        segment is "." or ".." ||
        segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
        segment.Contains(':', StringComparison.Ordinal);
}
