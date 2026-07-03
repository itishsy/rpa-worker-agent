using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Seebot.WorkerAgent.Core.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLoggerOptions _options;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly object _writeLock = new();

    public FileLoggerProvider(FileLoggerOptions options)
    {
        _options = options;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, category => new FileLogger(category, _options, _writeLock));
    }

    public void Dispose()
    {
        _loggers.Clear();
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly FileLoggerOptions _options;
        private readonly object _writeLock;

        public FileLogger(string categoryName, FileLoggerOptions options, object writeLock)
        {
            _categoryName = categoryName;
            _options = options;
            _writeLock = writeLock;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return _options.Enabled
                && logLevel != LogLevel.None
                && logLevel >= _options.MinimumLevel
                && !string.IsNullOrWhiteSpace(_options.DirectoryPath);
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception is null)
            {
                return;
            }

            var now = DateTimeOffset.Now;
            var line = FormatLine(now, logLevel, eventId, message, exception);
            var path = BuildLogPath(now);

            lock (_writeLock)
            {
                Directory.CreateDirectory(_options.DirectoryPath);
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }

        private string BuildLogPath(DateTimeOffset now)
        {
            var prefix = string.IsNullOrWhiteSpace(_options.FileNamePrefix)
                ? "rpa-worker-agent"
                : _options.FileNamePrefix.Trim();
            return Path.Combine(_options.DirectoryPath, $"{prefix}-{now:yyyyMMdd}.log");
        }

        private string FormatLine(
            DateTimeOffset timestamp,
            LogLevel logLevel,
            EventId eventId,
            string message,
            Exception? exception)
        {
            var eventPart = eventId.Id == 0 ? "" : $" [{eventId.Id}:{eventId.Name}]";
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{logLevel}] {_categoryName}{eventPart}: {message}");

            return exception is null
                ? line
                : line + Environment.NewLine + exception;
        }
    }
}
