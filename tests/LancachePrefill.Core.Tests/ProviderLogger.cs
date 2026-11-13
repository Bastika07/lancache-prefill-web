using Microsoft.Extensions.Logging;

namespace LancachePrefill.Core.Tests;

/// <summary>An <see cref="ILogger{T}"/> writing to one provider, as the host's LoggerFactory would.</summary>
internal sealed class ProviderLogger<T>(ILoggerProvider provider) : ILogger<T>
{
    private readonly ILogger _inner = provider.CreateLogger(typeof(T).FullName!);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);
    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _inner.Log(logLevel, eventId, state, exception, formatter);
}
