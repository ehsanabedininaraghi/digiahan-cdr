using System.Text.Json;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class VoipIncidentLogger
{
    private readonly string _root;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public VoipIncidentLogger(IWebHostEnvironment environment, IConfiguration configuration)
    {
        var configured = configuration["Logging:FileDirectory"] ?? "Logs";
        var basePath = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured);

        _root = Path.Combine(basePath, "Voip", "v4");
        Directory.CreateDirectory(_root);
    }

    public string Start(string requestId, string method, string path, string? remoteIp, string rawBody)
    {
        var runId = $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Sanitize(requestId)}";
        Write(runId, "REQUEST_RECEIVED", new
        {
            requestId,
            method,
            path,
            remoteIp,
            rawBody = Limit(rawBody, 12000)
        });
        return runId;
    }

    public void Write(string runId, string stage, object? data = null, Exception? exception = null)
    {
        var entry = new
        {
            utc = DateTime.UtcNow,
            runId,
            stage,
            data,
            exception = exception is null ? null : new
            {
                type = exception.GetType().FullName,
                exception.Message,
                stackTrace = exception.ToString(),
                inner = exception.InnerException?.ToString()
            }
        };

        var jsonLine = JsonSerializer.Serialize(entry);
        var pretty = JsonSerializer.Serialize(entry, JsonOptions);
        var dailyPath = Path.Combine(_root, $"voip-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
        var requestPath = Path.Combine(_root, $"{runId}.json");

        lock (_gate)
        {
            File.AppendAllText(dailyPath, jsonLine + Environment.NewLine);
            File.AppendAllText(requestPath, pretty + Environment.NewLine + Environment.NewLine);
        }
    }

    public string RootDirectory => _root;

    private static string Sanitize(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        return new string(chars);
    }

    private static string Limit(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...[truncated]";
}
