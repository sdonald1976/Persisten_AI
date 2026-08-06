using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Companion.Tests.Fixtures;

/// <summary>Captures log entries so tests can assert that server-side detail (incl. exceptions) is recorded.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public sealed record Entry(LogLevel Level, string Message, Exception? Exception);

    public ConcurrentQueue<Entry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Capturing(this);

    public void Dispose() { }

    private sealed class Capturing : ILogger
    {
        private readonly CapturingLoggerProvider _owner;
        public Capturing(CapturingLoggerProvider owner) => _owner = owner;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _owner.Entries.Enqueue(new Entry(logLevel, formatter(state, exception), exception));
    }
}
