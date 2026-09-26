using System.Diagnostics.Metrics;

namespace Decisya.ServiceDefaults.Logging;

/// <summary>
/// Self-observability for the masking core. Masking failures are silent by design (fail
/// closed, never rethrow, never log through <see cref="Microsoft.Extensions.Logging.ILogger"/>,
/// which could recurse), so they are surfaced as a metric instead.
/// </summary>
internal static class ObservabilityDiagnostics
{
    /// <summary>
    /// Matches the <c>Decisya.*</c> wildcard that ServiceDefaults registers, so the counter
    /// is exported without any extra wiring.
    /// </summary>
    internal const string MeterName = "Decisya.ServiceDefaults";

    internal const string MaskingErrorsCounterName = "decisya.observability.masking_errors";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> MaskingErrors = Meter.CreateCounter<long>(
        MaskingErrorsCounterName,
        unit: "{error}",
        description: "Failures inside the log masking core, by the rule that failed. The value was masked.");

    /// <summary>Records one masking failure, tagged with the rule that hit it.</summary>
    internal static void RecordMaskingError(string rule) =>
        MaskingErrors.Add(1, new KeyValuePair<string, object?>("rule", rule));
}
