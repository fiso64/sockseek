using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Sockseek.Server.Tests;

internal sealed record RecordedLog(
    LogLevel Level,
    EventId EventId,
    string Message,
    Exception? Exception);

internal sealed class RecordingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<RecordedLog> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Enqueue(new RecordedLog(
            logLevel,
            eventId,
            formatter(state, exception),
            exception));
}
