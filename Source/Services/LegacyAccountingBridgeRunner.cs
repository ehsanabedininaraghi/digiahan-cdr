using System.Diagnostics;
using System.Text.Json;

namespace DigiAhan.CDR.Receiver.Services;

public sealed record LegacyBridgeResult(string Status, int ExitCode, string Output, string Error);

public sealed class LegacyAccountingBridgeRunner
{
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<LegacyAccountingBridgeRunner> _logger;

    public LegacyAccountingBridgeRunner(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<LegacyAccountingBridgeRunner> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    public bool IsConfigured => _configuration.GetValue("DataGathering:AccountingEnabled", true);

    public async Task<LegacyBridgeResult> RunAsync(int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 365);
        var repositoryRoot = Directory.GetParent(_environment.ContentRootPath)?.FullName
            ?? throw new InvalidOperationException("Repository root could not be resolved.");
        var configuredPath = _configuration["DataGathering:AccountingBridgeScript"]
            ?? "tools/accounting-bridge-v4.3.10.ps1";
        var scriptPath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(repositoryRoot, configuredPath);

        if (!File.Exists(scriptPath))
            throw new FileNotFoundException("Legacy accounting bridge was not found.", scriptPath);

        var taskName = _configuration["DataGathering:AccountingBridgeTaskName"];
        if (!string.IsNullOrWhiteSpace(taskName))
            return await RunViaInteractiveTaskAsync(taskName, repositoryRoot, scriptPath, days, ct);

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
            "-RepositoryRoot", repositoryRoot, "-Days",
            days.ToString(System.Globalization.CultureInfo.InvariantCulture), "-SkipIdentityRebuild"
        })
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Legacy accounting bridge could not be started.");
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            _logger.LogError("Legacy accounting bridge failed. ExitCode={ExitCode} Error={Error}",
                process.ExitCode, string.IsNullOrWhiteSpace(error) ? output : error);
            return new LegacyBridgeResult("FAILED", process.ExitCode, output, error);
        }

        _logger.LogInformation("Legacy accounting bridge completed successfully.");
        return new LegacyBridgeResult("SUCCESS", process.ExitCode, output, error);
    }

    private async Task<LegacyBridgeResult> RunViaInteractiveTaskAsync(
        string taskName,
        string repositoryRoot,
        string scriptPath,
        int days,
        CancellationToken ct)
    {
        var exchangeDirectory = _configuration["DataGathering:AccountingBridgeExchangeDirectory"];
        if (string.IsNullOrWhiteSpace(exchangeDirectory))
            exchangeDirectory = Path.Combine(repositoryRoot, "runtime", "accounting-bridge");

        Directory.CreateDirectory(exchangeDirectory);
        var requestPath = Path.Combine(exchangeDirectory, "request.json");
        var resultPath = Path.Combine(exchangeDirectory, "result.json");
        var requestId = Guid.NewGuid().ToString("N");
        if (File.Exists(resultPath)) File.Delete(resultPath);

        var request = new InteractiveBridgeRequest(requestId, repositoryRoot, scriptPath, days);
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request), ct);

        var startInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("/Run");
        startInfo.ArgumentList.Add("/TN");
        startInfo.ArgumentList.Add(taskName);

        using (var launcher = Process.Start(startInfo)
               ?? throw new InvalidOperationException("Interactive accounting task could not be started."))
        {
            var launchOutput = launcher.StandardOutput.ReadToEndAsync(ct);
            var launchError = launcher.StandardError.ReadToEndAsync(ct);
            await launcher.WaitForExitAsync(ct);
            if (launcher.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Interactive accounting task launch failed ({launcher.ExitCode}): {await launchError} {await launchOutput}");
        }

        var timeoutSeconds = Math.Clamp(
            _configuration.GetValue("DataGathering:AccountingBridgeTaskTimeoutSeconds", 600), 30, 1800);
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(resultPath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(resultPath, ct);
                    var result = JsonSerializer.Deserialize<InteractiveBridgeResult>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (result is not null && string.Equals(result.RequestId, requestId, StringComparison.OrdinalIgnoreCase))
                    {
                        return new LegacyBridgeResult(
                            result.Status ?? "FAILED",
                            result.ExitCode,
                            result.Output ?? string.Empty,
                            result.Error ?? string.Empty);
                    }
                }
                catch (IOException)
                {
                    // The interactive process may still be atomically replacing the result file.
                }
                catch (JsonException)
                {
                    // Wait for the writer to finish if an antivirus scanner exposed the file early.
                }
            }

            await Task.Delay(500, ct);
        }

        throw new TimeoutException($"Interactive accounting task did not finish within {timeoutSeconds} seconds.");
    }

    private sealed record InteractiveBridgeRequest(
        string RequestId,
        string RepositoryRoot,
        string ScriptPath,
        int Days);

    private sealed record InteractiveBridgeResult(
        string RequestId,
        string? Status,
        int ExitCode,
        string? Output,
        string? Error);
}
