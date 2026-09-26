using Microsoft.Extensions.Logging;

namespace Decisya.ServiceDefaults.Logging;

/// <summary>
/// The compiled log messages ServiceDefaults itself writes. Source-generated so the
/// message template is fixed at compile time and no interpolation can smuggle a value
/// into it.
/// </summary>
/// <remarks>
/// ServiceDefaults never logs the value of <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>,
/// <c>OTEL_EXPORTER_OTLP_HEADERS</c> (which carries the collector's API key) or any other
/// <c>OTEL_*</c> variable, and never logs key material.
/// </remarks>
internal static partial class ObservabilityLog
{
    [LoggerMessage(
        EventId = 1500,
        Level = LogLevel.Warning,
        Message = "No {Setting} is configured. A per-process key was generated, so user_id hashes are " +
                  "per-process and do not correlate across hosts or restarts. This is Development-only behaviour.")]
    internal static partial void EphemeralUserIdHashKey(ILogger logger, string setting);
}
