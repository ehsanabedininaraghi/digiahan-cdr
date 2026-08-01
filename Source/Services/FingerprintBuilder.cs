using DigiAhan.CDR.Receiver.Models;
using System.Security.Cryptography;
using System.Text;

namespace DigiAhan.CDR.Receiver.Services;

public static class FingerprintBuilder
{
    public static string Build(CdrRecord record, string sourceServer)
    {
        if (!string.IsNullOrWhiteSpace(record.Fingerprint))
        {
            var supplied = record.Fingerprint.Trim().ToLowerInvariant();

            if (supplied.Length == 64 && supplied.All(Uri.IsHexDigit))
                return supplied;

            throw new ArgumentException("Fingerprint must be a 64-character SHA-256 hexadecimal string.");
        }

        var canonical = string.Join("|",
            Normalize(sourceServer),
            record.Calldate?.ToUniversalTime().ToString("O") ?? "",
            Normalize(record.UniqueId),
            Normalize(record.LinkedId),
            Normalize(record.Src),
            Normalize(record.Dst),
            record.Duration?.ToString() ?? "",
            record.Billsec?.ToString() ?? "",
            Normalize(record.Disposition),
            Normalize(record.Channel),
            Normalize(record.DstChannel),
            Normalize(record.SourceRowKey),
            record.SequenceNo?.ToString() ?? "");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Normalize(string? value) =>
        value?.Trim().ToLowerInvariant() ?? "";
}
