using DigiAhan.CDR.Receiver.Models;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DigiAhan.CDR.Receiver.Services;

public sealed partial class IssabelSftpRecordingClient
{
    private readonly IssabelRecordingPathResolver _pathResolver;

    public IssabelSftpRecordingClient(IssabelRecordingPathResolver pathResolver)
    {
        _pathResolver = pathResolver;
    }

    [GeneratedRegex(@"^\S+\s+\d+\s+\S+\s+\S+\s+(?<size>\d+)\s+", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex LongListingRegex();

    public async Task<RemoteRecordingInfo?> GetInfoAsync(
        RecordingIngestionOptions options,
        string recordingFile,
        DateTime callDate,
        CancellationToken ct)
    {
        ValidateConfiguration(options);
        var relative = _pathResolver.ResolveRelativePath(recordingFile, callDate);
        var full = _pathResolver.ResolveRemotePath(options.RemoteRoot, recordingFile, callDate);
        var first = await GetRemoteSizeAsync(options, full, ct);
        if (!first.HasValue) return null;

        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        var second = await GetRemoteSizeAsync(options, full, ct);
        if (!second.HasValue) return null;
        var stable = first.Value == second.Value;
        return new RemoteRecordingInfo(
            relative,
            full,
            second.Value,
            stable,
            DateTime.UtcNow);
    }

    public async Task DownloadAsync(
        RecordingIngestionOptions options,
        string remotePath,
        string localPartPath,
        CancellationToken ct)
    {
        ValidateConfiguration(options);
        var localForSftp = localPartPath.Replace('\\', '/');
        var result = await RunBatchAsync(
            options,
            $"get \"{remotePath}\" \"{localForSftp}\"{Environment.NewLine}",
            ct);
        if (result.ExitCode != 0 || !File.Exists(localPartPath))
            throw new IOException($"SFTP download failed: {Tail(result.Error + result.Output, 2000)}");
    }

    private static async Task<long?> GetRemoteSizeAsync(
        RecordingIngestionOptions options,
        string remotePath,
        CancellationToken ct)
    {
        var result = await RunBatchAsync(
            options,
            $"ls -l \"{remotePath}\"{Environment.NewLine}",
            ct);
        var combined = result.Output + Environment.NewLine + result.Error;
        if (result.ExitCode != 0 || combined.Contains("No such file", StringComparison.OrdinalIgnoreCase))
            return null;
        var match = LongListingRegex().Match(combined);
        if (!match.Success || !long.TryParse(match.Groups["size"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var size))
            throw new InvalidDataException($"Could not parse SFTP file size: {Tail(combined, 2000)}");
        return size;
    }

    private static async Task<ProcessResult> RunBatchAsync(
        RecordingIngestionOptions options,
        string batchContent,
        CancellationToken ct)
    {
        var batchPath = Path.Combine(Path.GetTempPath(), $"digiahan-sftp-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(batchPath, batchContent, new UTF8Encoding(false), ct);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = options.SftpExecutable,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            var arguments = process.StartInfo.ArgumentList;
            arguments.Add("-q");
            arguments.Add("-b");
            arguments.Add(batchPath);
            arguments.Add("-P");
            arguments.Add(Math.Clamp(options.Port, 1, 65535).ToString(CultureInfo.InvariantCulture));
            arguments.Add("-i");
            arguments.Add(options.PrivateKeyPath);
            arguments.Add("-oBatchMode=yes");
            arguments.Add("-oStrictHostKeyChecking=yes");
            arguments.Add($"-oUserKnownHostsFile={options.KnownHostsPath}");
            arguments.Add("-oConnectTimeout=10");
            arguments.Add($"{options.Username}@{options.Host}");

            if (!process.Start()) throw new InvalidOperationException("OpenSSH SFTP process did not start.");
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
        }
        finally
        {
            if (File.Exists(batchPath)) File.Delete(batchPath);
        }
    }

    private static void ValidateConfiguration(RecordingIngestionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Host) || string.IsNullOrWhiteSpace(options.Username))
            throw new InvalidOperationException("RecordingIngestion Host and Username are required.");
        if (string.IsNullOrWhiteSpace(options.PrivateKeyPath) || !File.Exists(options.PrivateKeyPath))
            throw new FileNotFoundException("Issabel read-only SFTP private key was not found.", options.PrivateKeyPath);
        if (string.IsNullOrWhiteSpace(options.KnownHostsPath) || !File.Exists(options.KnownHostsPath))
            throw new FileNotFoundException("Pinned OpenSSH known_hosts file was not found.", options.KnownHostsPath);
        if (string.IsNullOrWhiteSpace(options.SftpExecutable))
            throw new InvalidOperationException("RecordingIngestion:SftpExecutable is required.");
    }

    private static string Tail(string value, int maximum) =>
        value.Length <= maximum ? value : value[^maximum..];

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
