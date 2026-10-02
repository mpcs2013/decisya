namespace Decisya.Modules.Entitlements.Tests;

/// <summary>
/// ActivityListeners are process-wide: while any test listens to the Entitlements or Audit
/// <c>ActivitySource</c>, a handler creates an activity, and with it a trace id. The one scenario that
/// needs "no activity is current" (G1 Story 3: a missing activity leaves a null trace id) therefore
/// runs in a collection that never overlaps another test.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialTelemetryGroup
{
    public const string Name = "No other test may listen to telemetry while this one runs";
}
