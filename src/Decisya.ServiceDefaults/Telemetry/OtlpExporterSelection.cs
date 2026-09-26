using Microsoft.Extensions.Configuration;

namespace Decisya.ServiceDefaults.Telemetry;

/// <summary>
/// Decides whether the OTLP exporter is wired up. Factored out of
/// <c>AddOpenTelemetryExporters</c> so the rule can be unit tested without building a host
/// (Story 1 scenarios 1 and 2).
/// </summary>
internal static class OtlpExporterSelection
{
    /// <summary>
    /// The configuration key (an environment variable in practice, supplied by the Aspire
    /// AppHost) that selects the OTLP endpoint. No Decisya host ever hardcodes it.
    /// </summary>
    internal const string EndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>
    /// <see langword="true"/> when a non-blank OTLP endpoint is configured. When it is
    /// <see langword="false"/> the host starts normally and exports nothing.
    /// </summary>
    internal static bool IsEnabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return !string.IsNullOrWhiteSpace(configuration[EndpointKey]);
    }
}
