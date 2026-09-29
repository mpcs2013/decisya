using System.Collections.Concurrent;
using Decisya.ServiceDefaults.Logging;
using Microsoft.Extensions.Logging;

namespace Decisya.Api.Tests.Authentication;

/// <summary>One captured log record, with the ambient tenant id and hashed user id read live
/// from <see cref="ILogEnrichmentContext"/> at the moment of the log call — the same technique
/// <c>DecisyaJsonConsoleFormatter</c> uses to put those fields on the real stdout line (issue
/// #21, G3 G4-21-01).</summary>
internal sealed record EnrichedLogRecord(string Category, string? TenantId, string? UserIdHash, string StateText);

/// <summary>
/// An <see cref="ILoggerProvider"/> that resolves the host's own <see cref="ILogEnrichmentContext"/>
/// singleton through DI — never a fixture-local copy with its own <c>AsyncLocal</c> slot — so
/// its readings reflect exactly what <c>CallerContextMiddleware</c> set for the request each log
/// call happens inside.
/// </summary>
internal sealed class EnrichmentCapturingLoggerProvider(ILogEnrichmentContext enrichment) : ILoggerProvider
{
    internal ConcurrentQueue<EnrichedLogRecord> Records { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName, enrichment);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(
        EnrichmentCapturingLoggerProvider provider, string categoryName, ILogEnrichmentContext enrichment) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            var stateText = state is IEnumerable<KeyValuePair<string, object>> structuredState
                ? string.Join("; ", structuredState.Select(pair => $"{pair.Key}={pair.Value}"))
                : state?.ToString() ?? string.Empty;

            provider.Records.Enqueue(new EnrichedLogRecord(categoryName, enrichment.TenantId, enrichment.UserIdHash, stateText));
        }
    }
}
