using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Decisya.Api.Tests;

/// <summary>One captured log record: the rendered message, every structured state value
/// stringified, and the exception text, so a scan can check each independently (mirrors
/// <c>Decisya.Bff.Tests/CapturingLoggerProvider.cs</c>).</summary>
internal sealed record CapturedLogRecord(
    LogLevel Level, string Category, string Message, string StateText, string? ExceptionText, string? EventName = null)
{
    internal bool Contains(string value) =>
        Message.Contains(value, StringComparison.Ordinal)
        || StateText.Contains(value, StringComparison.Ordinal)
        || (ExceptionText?.Contains(value, StringComparison.Ordinal) ?? false);
}

/// <summary>
/// G4-20-05: an in-memory <see cref="ILoggerProvider"/> that captures every log record
/// forwarded to it, so a test can scan every message, structured state value and exception
/// text for a token, claim or secret leak, instead of trusting the product code's own
/// discipline by inspection alone.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    internal ConcurrentQueue<CapturedLogRecord> Records { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingLoggerProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            var message = formatter(state, exception);
            var stateText = state is IEnumerable<KeyValuePair<string, object>> structuredState
                ? string.Join("; ", structuredState.Select(pair => $"{pair.Key}={pair.Value}"))
                : state?.ToString() ?? string.Empty;

            provider.Records.Enqueue(new CapturedLogRecord(logLevel, categoryName, message, stateText, exception?.ToString(), eventId.Name));
        }
    }
}
