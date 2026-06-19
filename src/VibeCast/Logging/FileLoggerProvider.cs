using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace VibeCast.Logging;

/// <summary>
/// Minimal daily-rolling file logger (CLAUDE.md: logs/vibecast-YYYYMMDD.log), kept
/// dependency-free so the published artifact stays self-contained and portable.
/// Single writer lock -- log volume is low enough that contention isn't a concern.
/// </summary>
internal sealed class FileLoggerProvider(string logsDirectory) : ILoggerProvider
{
    private readonly Lock writeLock = new();
    private readonly ConcurrentDictionary<string, FileLogger> loggers = new();
    private StreamWriter? writer;
    private DateOnly? writerDate;

    public ILogger CreateLogger(string categoryName) =>
        loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    public void Dispose()
    {
        lock (writeLock)
        {
            writer?.Dispose();
            writer = null;
        }
    }

    internal void Write(string categoryName, LogLevel level, string message, Exception? exception)
    {
        var now = DateTime.Now;
        var line = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {categoryName}: {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        lock (writeLock)
        {
            var today = DateOnly.FromDateTime(now);
            if (writer is null || writerDate != today)
            {
                writer?.Dispose();
                Directory.CreateDirectory(logsDirectory);
                var path = Path.Combine(logsDirectory, $"vibecast-{today:yyyyMMdd}.log");
                writer = new StreamWriter(path, append: true) { AutoFlush = true };
                writerDate = today;
            }

            writer.WriteLine(line);
        }
    }

    private sealed class FileLogger(string categoryName, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider.Write(categoryName, logLevel, formatter(state, exception), exception);
        }
    }
}
