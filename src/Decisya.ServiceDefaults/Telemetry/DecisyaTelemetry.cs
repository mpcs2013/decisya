namespace Decisya.ServiceDefaults.Telemetry;

/// <summary>
/// Telemetry naming constants shared by <c>Decisya.ServiceDefaults</c>' tracer and meter
/// wiring. Every module declares exactly one <c>ActivitySource</c> and one <c>Meter</c>
/// named <c>Decisya.&lt;Module&gt;</c>, which the wildcard below picks up without any
/// change to ServiceDefaults.
/// </summary>
internal static class DecisyaTelemetry
{
    /// <summary>
    /// The prefix every Decisya <c>ActivitySource</c> and <c>Meter</c> name starts with.
    /// The trailing dot is part of the constant on purpose: a source called
    /// <c>DecisyaFoo</c> must not be picked up by the wildcard below.
    /// </summary>
    internal const string SourcePrefix = "Decisya.";

    /// <summary>
    /// The wildcard handed to <c>AddSource</c> and <c>AddMeter</c>.
    /// </summary>
    internal const string SourceWildcard = SourcePrefix + "*";

    /// <summary>
    /// The prefix of the Ostium identity packages' <c>ActivitySource</c> and <c>Meter</c>
    /// names (ADR-0019). Subscribed by name only: ServiceDefaults has no package reference to
    /// Ostium. The trailing dot keeps <c>OstiumFoo</c> out. Ostium 0.1.1 declares neither a
    /// source nor a meter; the subscription is ready for a later release.
    /// </summary>
    internal const string OstiumSourcePrefix = "Ostium.";

    /// <summary>
    /// The Ostium wildcard handed to <c>AddSource</c> and <c>AddMeter</c>.
    /// </summary>
    internal const string OstiumSourceWildcard = OstiumSourcePrefix + "*";
}
