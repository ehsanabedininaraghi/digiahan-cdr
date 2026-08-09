using DigiAhan.CDR.Receiver.Models;
using System.Security.Cryptography;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class RecordingAudioValidator
{
    public async Task<ValidatedRecording> ValidateWavAsync(
        string path,
        long expectedSize,
        CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 12)
            throw new InvalidDataException("Downloaded recording is empty or too short.");
        if (expectedSize <= 0 || info.Length != expectedSize)
            throw new InvalidDataException($"Downloaded size {info.Length} does not match remote size {expectedSize}.");

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = new byte[12];
        await stream.ReadExactlyAsync(header, ct);
        if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !header.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Downloaded file is not a RIFF/WAVE recording.");

        stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, ct);
        return new ValidatedRecording(info.Length, Convert.ToHexString(hash).ToLowerInvariant());
    }
}
