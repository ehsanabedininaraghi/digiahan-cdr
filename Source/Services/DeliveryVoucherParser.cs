using System.Text;
using System.Text.RegularExpressions;

namespace DigiAhan.CDR.Receiver.Services;

public static partial class DeliveryVoucherParser
{
    [GeneratedRegex(@"(?:حواله)\s*[:：\-]?\s*([0-9۰-۹٠-٩]+(?:\s*/\s*[0-9۰-۹٠-٩]+)*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VoucherRegex();

    public static string? Parse(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var match = VoucherRegex().Match(description);
        if (!match.Success) return null;

        var value = new StringBuilder(match.Groups[1].Value.Length);
        foreach (var ch in match.Groups[1].Value)
        {
            if (char.IsWhiteSpace(ch)) continue;
            value.Append(ch switch
            {
                >= '\u06F0' and <= '\u06F9' => (char)('0' + ch - '\u06F0'),
                >= '\u0660' and <= '\u0669' => (char)('0' + ch - '\u0660'),
                _ => ch
            });
        }
        return value.ToString();
    }
}
