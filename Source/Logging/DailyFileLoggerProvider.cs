using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text;

namespace DigiAhan.CDR.Receiver.Logging;

public sealed class DailyFileLoggerProvider : ILoggerProvider
{
    private readonly string _logDirectory;
    private readonly ConcurrentDictionary<string, DailyFileLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);

    public DailyFileLoggerProvider(string logDirectory)
    {
        _logDirectory = Path.GetFullPath(logDirectory);
        Directory.CreateDirectory(_logDirectory);
    }

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, category => new DailyFileLogger(_logDirectory, category));

    public void Dispose() => _loggers.Clear();

    private sealed class DailyFileLogger : ILogger
    {
        private static readonly object WriteLock = new();
        private readonly string _directory;
        private readonly string _category;

        public DailyFileLogger(string directory, string category)
        {
            _directory = directory;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var now = DateTimeOffset.Now;
            var file = Path.Combine(_directory, $"app-{now:yyyy-MM-dd}.log");
            var message = formatter(state, exception);
            var sb = new StringBuilder()
                .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append(" [").Append(logLevel).Append("] ")
                .Append(_category).Append(" | ")
                .AppendLine(message);

            if (exception is not null)
                sb.AppendLine(exception.ToString());

            lock (WriteLock)
            {
                File.AppendAllText(file, sb.ToString(), new UTF8Encoding(false));
            }
        }
    }
}
