using Decisya.ServiceDefaults.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;

namespace Decisya.Modules.Audit.Tests.TestSupport;

/// <summary>
/// Issue #230 (G5): writes one log record the way the host does, through the real
/// <see cref="DecisyaJsonConsoleFormatter"/> and <see cref="SensitiveDataMaskingProcessor"/>, and returns the
/// emitted stdout line. The value is passed whole as structured state, under a placeholder name that the key
/// deny-list does not match, so only <c>[Sensitive]</c> can mask it. The formatter's own message function
/// renders the value with its <c>ToString</c> (the unmasked, naive path), so a leak on that path would show.
/// </summary>
internal static class MaskedLogLine
{
    internal static string Write(string placeholder, object value)
    {
        var formatter = new DecisyaJsonConsoleFormatter(new FixedClock(), new SensitiveDataMaskingProcessor(), new NoEnrichment());
        var template = "Seen {" + placeholder + "}";
        IReadOnlyList<KeyValuePair<string, object?>> state =
        [
            new KeyValuePair<string, object?>(placeholder, value),
            new KeyValuePair<string, object?>("{OriginalFormat}", template),
        ];
        var entry = new LogEntry<IReadOnlyList<KeyValuePair<string, object?>>>(
            LogLevel.Information,
            "Decisya.Test.Category",
            new EventId(1, "TestEvent"),
            state,
            null,
            (_, _) => $"Seen {value}");

        using var writer = new StringWriter();
        formatter.Write(in entry, scopeProvider: null, writer);
        return writer.ToString();
    }

    private sealed class FixedClock : IClock
    {
        public Instant GetCurrentInstant() => Instant.FromUtc(2026, 1, 1, 0, 0);
    }

    private sealed class NoEnrichment : ILogEnrichmentContext
    {
        public string? TenantId => null;

        public string? UserIdHash => null;

        public IDisposable Begin(string? tenantId, string? userId) => new NoScope();

        private sealed class NoScope : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
