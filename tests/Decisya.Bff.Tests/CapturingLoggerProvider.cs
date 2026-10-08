using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Decisya.Bff.Tests;

/// <summary>One captured log record: the rendered message, every structured state value
/// stringified, and the exception text, so a scan can check each independently (a token value
/// could land in any of the three).</summary>
internal sealed record CapturedLogRecord(
    LogLevel Level, string Category, string Message, string StateText, string? ExceptionText, int EventId = 0, string? EventName = null,
    string? UserIdHash = null, string? TenantId = null)
{
    internal bool Contains(string value) =>
        Message.Contains(value, StringComparison.Ordinal)
        || StateText.Contains(value, StringComparison.Ordinal)
        || (ExceptionText?.Contains(value, StringComparison.Ordinal) ?? false);
}

/// <summary>
/// #19 G3 MUST G4-19-05: an in-memory <see cref="ILoggerProvider"/> that captures every log
/// record forwarded to it (paired with <see cref="BffWebApplicationFactory"/>'s
/// <c>loggerProvider</c> parameter, which forces every category to <see cref="LogLevel.Debug"/>)
/// so a test can scan every message, structured state value and exception text for a token,
/// secret or session-key leak, instead of trusting <c>Decisya.Bff.Session.BffLog</c>'s own
/// message templates by inspection alone.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    internal ConcurrentQueue<CapturedLogRecord> Records { get; } = new();

    /// <summary>
    /// #121: when set (after the host exists), every record also captures the ambient hashed
    /// <c>user_id</c> and <c>tenant_id</c> at the moment of the log call, the same technique the
    /// Api tests use, so a test can prove which fields an event carries and that none go stale.
    /// </summary>
    internal Decisya.ServiceDefaults.Logging.ILogEnrichmentContext? Enrichment { get; set; }

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

            provider.Records.Enqueue(new CapturedLogRecord(
                logLevel, categoryName, message, stateText, exception?.ToString(), eventId.Id, eventId.Name,
                provider.Enrichment?.UserIdHash, provider.Enrichment?.TenantId));
        }
    }
}
