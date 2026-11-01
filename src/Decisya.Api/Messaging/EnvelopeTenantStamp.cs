using Decisya.Api.Authentication;
using Decisya.SharedKernel.Tenancy;
using Wolverine;

namespace Decisya.Api.Messaging;

/// <summary>
/// The one writer of the envelope tenant and caller id (issue #231, ADR-0024 point 4; G3
/// G4-231-01): the <see cref="IMessageBus"/> every scope gets. It wraps Wolverine's own scoped bus
/// and stamps every call that carries an envelope.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>It reads the scope's ambient tenant and caller on every call and never caches them. The
/// ambient is the scope's <see cref="RequestCaller"/>, set once by <c>CallerContextMiddleware</c> (or,
/// for a message scope, by <see cref="EnvelopeTenantMiddleware"/>).</item>
/// <item>It throws when there is no ambient tenant (<c>None</c> or <c>Invalid</c>), so a missing
/// tenant fails at the sender, as <c>TenantIdJsonConverter.Write</c> does.</item>
/// <item>It <b>overwrites</b> whatever tenant and caller-id header the caller's
/// <see cref="DeliveryOptions"/> already carries (in place), so a forged value never reaches the
/// envelope.</item>
/// <item>It writes only the canonical tenant text and the caller id: never a token, a cookie, a
/// claim set, a role or an MFA fact.</item>
/// <item>It does not offer what would name another tenant or reach a transport:
/// <c>InvokeForTenantAsync</c> and a set <c>TenantId</c> throw
/// <see cref="NotSupportedException"/>; <c>EndpointFor</c> and <c>BroadcastToTopicAsync</c> throw too.
/// A cross-tenant send for an explicit target arrives with the first cross-tenant sender (ADR-0024
/// point 4), under its own review.</item>
/// </list>
/// <para>
/// Finding at G4: in <c>DurabilityMode.MediatorOnly</c> Wolverine 6.39.1 never applies its
/// <c>IEnvelopeRule</c>s to <c>InvokeAsync</c> (the one send path that works there) and rejects
/// <c>PublishAsync</c>, <c>SendAsync</c>, scheduling and cascading outright, so the stamp is a bus
/// wrapper rather than a rule. Inside a generated handler the bus is Wolverine's own context, which
/// takes its tenant from the handled envelope (the one the receive middleware validated); no module
/// may reference Wolverine in #231 (architecture rule R5), and no Api type other than this one may
/// name a Wolverine member whose name contains <c>Tenant</c> (rule R6).
/// </para>
/// </remarks>
internal sealed class EnvelopeTenantStamp(IMessageBus inner, RequestCaller caller) : IMessageBus
{
    private const string NoAmbient =
        "A message can only be sent with an ambient tenant and caller: from a request scope after the " +
        "caller context ran, or from a message scope after the envelope tenant middleware ran. " +
        "No tenant is set for this sender.";

    public string? TenantId
    {
        get => caller.Resolution.Kind == TenantResolutionKind.Tenant ? caller.Resolution.TenantId.ToString() : null;
        set => throw new NotSupportedException("The envelope tenant is the scope's ambient tenant; it is never set by hand.");
    }

    public Task InvokeAsync(object message, CancellationToken cancellation = default, TimeSpan? timeout = null) =>
        inner.InvokeAsync(message, Stamp(null), cancellation, timeout);

    public Task InvokeAsync(object message, DeliveryOptions options, CancellationToken cancellation = default, TimeSpan? timeout = null) =>
        inner.InvokeAsync(message, Stamp(options), cancellation, timeout);

    public Task<T> InvokeAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = null) =>
        inner.InvokeAsync<T>(message, Stamp(null), cancellation, timeout);

    public Task<T> InvokeAsync<T>(object message, DeliveryOptions options, CancellationToken cancellation = default, TimeSpan? timeout = null) =>
        inner.InvokeAsync<T>(message, Stamp(options), cancellation, timeout);

    public IAsyncEnumerable<TResponse> StreamAsync<TResponse>(object message, CancellationToken cancellation = default) =>
        inner.StreamAsync<TResponse>(message, Stamp(null), cancellation);

    public IAsyncEnumerable<TResponse> StreamAsync<TResponse>(object message, DeliveryOptions options, CancellationToken cancellation = default) =>
        inner.StreamAsync<TResponse>(message, Stamp(options), cancellation);

    public Task<TResponse> StreamAsync<TRequest, TResponse>(IAsyncEnumerable<TRequest> messages, CancellationToken cancellation = default, TimeSpan? timeout = null) =>
        inner.StreamAsync<TRequest, TResponse>(messages, Stamp(null), cancellation, timeout);

    public Task<TResponse> StreamAsync<TRequest, TResponse>(IAsyncEnumerable<TRequest> messages, DeliveryOptions options, CancellationToken cancellation = default, TimeSpan? timeout = null) =>
        inner.StreamAsync<TRequest, TResponse>(messages, Stamp(options), cancellation, timeout);

    public ValueTask SendAsync<T>(T message, DeliveryOptions? options = null) =>
        inner.SendAsync(message, Stamp(options));

    public ValueTask PublishAsync<T>(T message, DeliveryOptions? options = null) =>
        inner.PublishAsync(message, Stamp(options));

    public IReadOnlyList<Envelope> PreviewSubscriptions(object message) =>
        inner.PreviewSubscriptions(message, Stamp(null));

    public IReadOnlyList<Envelope> PreviewSubscriptions(object message, DeliveryOptions options) =>
        inner.PreviewSubscriptions(message, Stamp(options));

    public Task InvokeForTenantAsync(string tenantId, object message, CancellationToken cancellation = default, TimeSpan? timeout = null) =>
        throw new NotSupportedException("A message for an explicit target tenant arrives with the first cross-tenant sender.");

    public Task<T> InvokeForTenantAsync<T>(string tenantId, object message, CancellationToken cancellation = default, TimeSpan? timeout = null) =>
        throw new NotSupportedException("A message for an explicit target tenant arrives with the first cross-tenant sender.");

    public IDestinationEndpoint EndpointFor(string endpointName) =>
        throw new NotSupportedException("No transport or endpoint exists in this host.");

    public IDestinationEndpoint EndpointFor(Uri uri) =>
        throw new NotSupportedException("No transport or endpoint exists in this host.");

    public ValueTask BroadcastToTopicAsync(string topicName, object message, DeliveryOptions? options = null) =>
        throw new NotSupportedException("No transport or topic exists in this host.");

    /// <summary>
    /// Stamps the ambient tenant and caller into the options (created when the caller passed none),
    /// overwriting whatever they held.
    /// </summary>
    private DeliveryOptions Stamp(DeliveryOptions? options)
    {
        var resolution = caller.Resolution;
        if (resolution.Kind != TenantResolutionKind.Tenant)
        {
            throw new InvalidOperationException(NoAmbient);
        }

        options ??= new DeliveryOptions();
        options.TenantId = resolution.TenantId.ToString();
        options.Headers[EnvelopeFacts.CallerIdHeader] = caller.UserId;
        return options;
    }
}
