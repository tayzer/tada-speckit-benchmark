using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CheckoutReload.HiddenTests.Infrastructure;

/// <summary>Captures error-level application log entries so evaluation failures carry diagnostics.</summary>
public sealed class ErrorLogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public string Summary(int max = 3) =>
        _entries.IsEmpty ? "(none)" : string.Join(" || ", _entries.Take(max).Select(e => e.Length > 600 ? e[..600] + "…" : e));

    public void Dispose() { }

    private sealed class Logger(ErrorLogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            owner._entries.Enqueue($"{category}: {formatter(state, exception)} {exception?.GetType().Name}: {exception?.Message}");
        }
    }
}
