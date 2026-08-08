using DigiAhan.CDR.Receiver.Models;
using System.Diagnostics;
using System.Text.Json;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class FasterWhisperTranscriber
{
    private readonly IWebHostEnvironment _environment;

    public FasterWhisperTranscriber(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        RecordingTranscriptionOptions options,
        string audioPath,
        CancellationToken ct)
    {
        var script = ResolvePath(options.ScriptPath);
        var modelCache = ResolvePath(options.ModelCache);
        if (!File.Exists(script))
            throw new FileNotFoundException("Faster-Whisper runner script was not found.", script);
        Directory.CreateDirectory(modelCache);

        var outputPath = Path.Combine(
            Path.GetDirectoryName(audioPath)!,
            $"transcript-{Guid.NewGuid():N}.json");
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = options.PythonExecutable,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            var arguments = process.StartInfo.ArgumentList;
            arguments.Add(script);
            arguments.Add(audioPath);
            arguments.Add("--model");
            arguments.Add(options.ModelName);
            arguments.Add("--output");
            arguments.Add(outputPath);
            arguments.Add("--model-cache");
            arguments.Add(modelCache);
            arguments.Add("--threads");
            arguments.Add(Math.Clamp(options.Threads, 1, 32).ToString(System.Globalization.CultureInfo.InvariantCulture));
            arguments.Add("--quiet");
            AddOptional(arguments, "--initial-prompt", options.InitialPrompt);
            AddOptional(arguments, "--hotwords", options.Hotwords);
            if (!string.IsNullOrWhiteSpace(options.PythonPath))
                process.StartInfo.Environment["PYTHONPATH"] = ResolvePath(options.PythonPath);

            if (!process.Start())
                throw new InvalidOperationException("Faster-Whisper process did not start.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(options.TimeoutMinutes, 1, 240)));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Faster-Whisper failed with exit code {process.ExitCode}: {Tail(stderr, 2000)}");
            if (!File.Exists(outputPath))
                throw new InvalidOperationException($"Faster-Whisper produced no JSON output. {Tail(stdout, 1000)}");

            await using var input = File.OpenRead(outputPath);
            var payload = await JsonSerializer.DeserializeAsync<WhisperPayload>(input, JsonOptions, ct)
                ?? throw new InvalidDataException("Faster-Whisper JSON output is empty.");
            var segments = payload.Segments ?? [];
            var transcript = string.Join(' ', segments
                .Select(segment => segment.Text?.Trim())
                .Where(text => !string.IsNullOrWhiteSpace(text)));
            var segmentsJson = JsonSerializer.Serialize(segments, JsonOptions);
            var speechSeconds = segments.Sum(segment => Math.Max(0, segment.End - segment.Start));
            return new TranscriptionResult(
                transcript,
                segmentsJson,
                payload.LanguageDetected ?? "fa",
                Convert.ToDecimal(payload.DurationSeconds),
                Convert.ToDecimal(speechSeconds),
                Convert.ToDecimal(payload.TranscriptionSeconds),
                payload.Engine ?? "faster-whisper",
                payload.Model ?? options.ModelName);
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    private string ResolvePath(string path) => Path.GetFullPath(
        Path.IsPathRooted(path) ? path : Path.Combine(_environment.ContentRootPath, path));

    private static void AddOptional(System.Collections.ObjectModel.Collection<string> arguments, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        arguments.Add(name);
        arguments.Add(value);
    }

    private static string Tail(string value, int maximum) =>
        value.Length <= maximum ? value : value[^maximum..];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private sealed class WhisperPayload
    {
        public string? Engine { get; set; }
        public string? Model { get; set; }
        public string? LanguageDetected { get; set; }
        public double DurationSeconds { get; set; }
        public double TranscriptionSeconds { get; set; }
        public List<WhisperSegment>? Segments { get; set; }
    }

    private sealed class WhisperSegment
    {
        public double Start { get; set; }
        public double End { get; set; }
        public string? Text { get; set; }
        public double AvgLogprob { get; set; }
        public double NoSpeechProb { get; set; }
        public JsonElement Words { get; set; }
    }
}
