using Microsoft.Extensions.Logging;

namespace LancachePrefill.Core;

public sealed record JobLogLine(DateTimeOffset At, LogLevel Level, string Category, string Message);

/// <summary>
/// Routes everything logged while a Job runs (by the Job queue and the Module, including work it
/// starts in parallel) into that Job's log. Register it with the host's logging; the Job queue
/// marks the running Job via <see cref="BeginJob"/>.
/// </summary>
public sealed class JobLogProvider(TimeProvider? time = null) : ILoggerProvider
{
    private static readonly AsyncLocal<Job?> Current = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Until disposed, log entries from this async flow go to <paramref name="job"/>.</summary>
    internal static IDisposable BeginJob(Job job)
    {
        var previous = Current.Value;
        Current.Value = job;
        return new Restore(previous);
    }

    public ILogger CreateLogger(string categoryName) => new JobLogger(categoryName, _time);

    public void Dispose() { }

    private sealed class Restore(Job? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }

    private sealed class JobLogger(string category, TimeProvider time) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && Current.Value is not null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (Current.Value is not { } job || logLevel == LogLevel.None)
            {
                return;
            }
            var message = formatter(state, exception);
            if (exception is not null)
            {
                message = $"{message}: {exception.Message}";
            }
            job.AppendLog(new JobLogLine(time.GetUtcNow(), logLevel, category, message));
        }
    }
}
