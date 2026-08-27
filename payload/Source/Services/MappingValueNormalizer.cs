using System.Globalization;
using System.Text;

namespace DigiAhan.CDR.Receiver.Services;

public static class MappingValueNormalizer
{
    public static bool TryAccountingCode(string? value, out string code, out string? error)
    {
        code = string.Empty;
        error = null;
        var normalized = ToAsciiDigits(value).Trim().Replace(" ", string.Empty);

        if (normalized.Contains('/'))
        {
            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            normalized = parts.FirstOrDefault(x => x != "30") ?? string.Empty;
        }

        var digits = new string(normalized.Where(char.IsDigit).ToArray());
        if (digits.Length is < 1 or > 6)
        {
            error = "Accounting code must contain between 1 and 6 digits.";
            return false;
        }

        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric) || numeric <= 0)
        {
            error = "Accounting code 000000 is not valid.";
            return false;
        }

        code = numeric.ToString("D6", CultureInfo.InvariantCulture);
        return true;
    }

    public static string? Phone(string? value)
    {
        var digits = new string(ToAsciiDigits(value).Where(char.IsDigit).ToArray());
        if (digits.StartsWith("0098")) digits = digits[4..];
        else if (digits.StartsWith("98") && digits.Length > 10) digits = digits[2..];
        if (digits.Length == 10 && digits.StartsWith('9')) digits = "0" + digits;
        return digits.Length >= 7 ? digits : null;
    }

    private static string ToAsciiDigits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var result = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            result.Append(ch switch
            {
                >= '\u06F0' and <= '\u06F9' => (char)('0' + ch - '\u06F0'),
                >= '\u0660' and <= '\u0669' => (char)('0' + ch - '\u0660'),
                _ => ch
            });
        }
        return result.ToString();
    }
}
