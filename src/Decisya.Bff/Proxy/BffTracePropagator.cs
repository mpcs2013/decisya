using System.Diagnostics;

namespace Decisya.Bff.Proxy;

/// <summary>
/// #27 B-01 (#15 T-20 / F-4): the BFF is the trust boundary between the browser and everything
/// behind it, so a browser-chosen trace id, <c>tracestate</c> or <c>baggage</c> is data, never
/// context. Registered as the <see cref="DistributedContextPropagator"/> the ASP.NET Core
/// hosting diagnostics read from the service provider, it extracts nothing: every request the
/// BFF handles starts a new trace root with empty baggage. Injection is the runtime's default
/// W3C behaviour, so the BFF's own context still reaches the Api and Keycloak.
/// </summary>
/// <remarks>
/// Only the hosting diagnostics see this instance (it is registered in the BFF's own service
/// provider, not assigned to <see cref="DistributedContextPropagator.Current"/>), so the Api
/// keeps normal propagation. Outbound <c>HttpClient</c> calls still inject through the default
/// propagator from the BFF's own activity.
/// </remarks>
internal sealed class BffTracePropagator : DistributedContextPropagator
{
    private readonly DistributedContextPropagator _default = CreateDefaultPropagator();

    public override IReadOnlyCollection<string> Fields => _default.Fields;

    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter) =>
        _default.Inject(activity, carrier, setter);

    public override void ExtractTraceIdAndState(
        object? carrier, PropagatorGetterCallback? getter, out string? traceId, out string? traceState)
    {
        traceId = null;
        traceState = null;
    }

    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(
        object? carrier, PropagatorGetterCallback? getter) => null;
}
