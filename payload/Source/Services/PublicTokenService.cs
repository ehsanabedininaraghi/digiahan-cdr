using System.Security.Cryptography;

namespace DigiAhan.CDR.Receiver.Services;

public static class PublicTokenService
{
    public static string Create()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static byte[] Hash(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));

    public static bool IsWellFormed(string? token)
        => !string.IsNullOrWhiteSpace(token)
           && token.Length is >= 40 and <= 64
           && token.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');
}
