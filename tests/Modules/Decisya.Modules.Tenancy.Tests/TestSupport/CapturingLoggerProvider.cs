using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Decisya.Modules.Tenancy.Tests.TestSupport;

/// <summary>One captured log record: the rendered message, every structured state value
/// stringified, and the exception text, so a scan can check each independently (mirrors
/// <c>Decisya.Api.Tests/CapturingLoggerProvider.cs</c>).</summary>
public sealed record CapturedLogRecord(LogLevel Level, string Category, string Message, string StateText, string? ExceptionText)
{
    /// <summary><see langword="true"/> if <paramref name="value"/> appears in the message, the
    /// structured state or the exception text.</summary>
    public bool Contains(string value) =>
        Message.Contains(value, StringComparison.Ordinal)
        || StateText.Contains(value, StringComparison.Ordinal)
        || (ExceptionText?.Contains(value, StringComparison.Ordinal) ?? false);
}

/// <summary>
/// G3 G4-21-04: an in-memory <see cref="ILoggerProvider"/> that captures every log record
/// forwarded to it — including EF Core's and Npgsql's own diagnostic logging, when wired into a
/// <c>DbContextOptionsBuilder.UseLoggerFactory</c> — so a race test can prove that not one
/// captured record, at <c>Debug</c>, carries a raw <c>sub</c> or another tenant's id.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<CapturedLogRecord> Records { get; } = new();

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

            provider.Records.Enqueue(new CapturedLogRecord(logLevel, categoryName, message, stateText, exception?.ToString()));
        }
    }
}
