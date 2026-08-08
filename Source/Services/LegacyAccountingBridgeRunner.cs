using System.Diagnostics;

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
            ?? "tools/accounting-bridge-v4.3.1.ps1";
        var scriptPath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(repositoryRoot, configuredPath);

        if (!File.Exists(scriptPath))
            throw new FileNotFoundException("Legacy accounting bridge was not found.", scriptPath);

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
}
