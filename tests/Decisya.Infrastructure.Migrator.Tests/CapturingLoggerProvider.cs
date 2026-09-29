using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Decisya.Infrastructure.Migrator.Tests;

/// <summary>One captured log record: the rendered message and every structured state value
/// stringified, so a scan can check each independently. Mirrors
/// <c>tests/Decisya.Bff.Tests/CapturingLoggerProvider.cs</c> (#19 G4-19-05) and
/// <c>tests/Decisya.Api.Tests/CapturingLoggerProvider.cs</c> — each test project keeps its own
/// copy rather than a shared reference (existing codebase precedent).</summary>
internal sealed record CapturedLogRecord(LogLevel Level, string Category, string Message, string StateText, string? ExceptionText)
{
    internal bool Contains(string value) =>
        Message.Contains(value, StringComparison.Ordinal)
        || StateText.Contains(value, StringComparison.Ordinal)
        || (ExceptionText?.Contains(value, StringComparison.Ordinal) ?? false);
}

/// <summary>
/// An in-memory <see cref="ILoggerProvider"/> that captures every log record forwarded to it, so
/// a test can scan every message and structured state value for a password or connection string
/// leak, instead of trusting <see cref="MigratorLog"/>'s own message templates by inspection
/// alone (issue #21, G3 G4-21-05, T-14).
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

            provider.Records.Enqueue(new CapturedLogRecord(logLevel, categoryName, message, stateText, exception?.ToString()));
        }
    }
}
