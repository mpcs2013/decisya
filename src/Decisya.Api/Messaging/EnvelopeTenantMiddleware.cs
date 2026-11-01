using System.Diagnostics;
using Decisya.Api.Authentication;
using Decisya.ServiceDefaults.Logging;
using Decisya.SharedKernel.Tenancy;
using Wolverine;

namespace Decisya.Api.Messaging;

/// <summary>
/// The first frame of every handler chain (issue #231, ADR-0024 point 3; G3 G4-231-02): reads the
/// envelope tenant and caller id, refuses a message that carries no valid pair, and otherwise
/// populates the message scope's <see cref="RequestCaller"/> before the handler runs. Public only
/// because Wolverine's generated code, which lives in another assembly, constructs it; nothing else
/// references it.
/// </summary>
/// <remarks>
/// <para>
/// Control (ADR-0024 amendment 2; G3 G4-231-08): no tenant-scoped read or write, and no read of the
/// caller context, happens before this middleware has set the scope's <see cref="RequestCaller"/>.
/// WolverineFx 6.39.1 builds a handler's scoped dependencies at the top of the generated method,
/// <b>before</b> <see cref="Before"/> runs, on a refused message too. So construction is allowed and
/// use is not: a tenant-dependent service keeps the <see cref="ICurrentTenant"/> /
/// <see cref="ICurrentCaller"/> reference and reads it at use time, never in its constructor or a DI
/// factory (architecture rule R16), and <c>TenantDbContext</c> throws under <c>Invalid</c>. On a
/// refusal the handler never runs, so nothing uses those dependencies, and the scope's
/// <see cref="RequestCaller"/> stays <c>Invalid</c>. Constructors stay free of I/O for the same reason.
/// </para>
/// <list type="bullet">
/// <item>The envelope tenant must be the exact canonical text (see <see cref="EnvelopeFacts.TryReadTenant"/>).</item>
/// <item>The caller id only attributes the work. The platform-admin and MFA facts are always
/// <see langword="false"/> for a message: neither is ever read from an envelope.</item>
/// <item>A refusal returns <see cref="HandlerContinuation.Stop"/>: no exception, so no retry and no
/// dead letter, and the handler never runs (its dependencies may already be built, see above, but
/// are never used). It writes one log event and adds one to the
/// refusal metric, with the reason as the metric's only tag. It applies to every handler, with or
/// without <c>[AllowCrossTenant]</c>: there is no tenant-less handler.</item>
/// <item>The scope's <see cref="ILogEnrichmentContext"/> begins with the tenant and the caller id
/// (stored hashed) for the handler's duration, so handler logs carry <c>tenant_id</c> and
/// <c>user_id</c> like HTTP logs do.</item>
/// </list>
/// Handlers never scope work by a tenant member of the message, only by <see cref="ICurrentTenant"/>.
/// </remarks>
public sealed class EnvelopeTenantMiddleware(
    ICurrentTenant currentTenant,
    ILogEnrichmentContext enrichment,
    ILogger<EnvelopeTenantMiddleware> logger)
{
    private const int MaxLoggedTypeNameLength = 256;

    private IDisposable? _enrichmentScope;

    public HandlerContinuation Before(Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!EnvelopeFacts.TryReadTenant(envelope.TenantId, out var tenant, out var tenantReason))
        {
            return Refuse(envelope, tenantReason.Value);
        }

        if (!EnvelopeFacts.TryReadCaller(envelope, out var callerId))
        {
            return Refuse(envelope, EnvelopeRefusalReason.NoCaller);
        }

        var requestCaller = currentTenant as RequestCaller
            ?? throw new InvalidOperationException(
                "The envelope tenant middleware needs the scoped RequestCaller registered by AddDecisyaApiAuthentication.");

        var resolution = TenantResolution.For(tenant);
        if (requestCaller.Resolution.Kind == TenantResolutionKind.Invalid)
        {
            // The message scope's RequestCaller is fresh: it is set exactly once, here, and the
            // platform-admin and MFA facts are false by construction.
            requestCaller.Set(resolution, callerId, isPlatformAdmin: false, hasMfaLevel: false);
        }
        else if (requestCaller.Resolution != resolution
            || !string.Equals(requestCaller.UserId, callerId, StringComparison.Ordinal)
            || requestCaller.IsPlatformAdmin
            || requestCaller.HasMfaLevel)
        {
            // A scope that was already populated with a different tenant or caller is never
            // re-resolved from an envelope, and one that carries an admin or MFA fact never serves a
            // message: both are always false for a message (G6-231-01).
            return Refuse(envelope, EnvelopeRefusalReason.Malformed);
        }

        _enrichmentScope = enrichment.Begin(tenant.ToString(), callerId);
        return HandlerContinuation.Continue;
    }

    public void Finally()
    {
        _enrichmentScope?.Dispose();
        _enrichmentScope = null;
    }

    private HandlerContinuation Refuse(Envelope envelope, EnvelopeRefusalReason reason)
    {
        EnvelopeTenantLog.EnvelopeRefused(
            logger,
            DescribeMessageType(envelope),
            envelope.Id,
            EnvelopeRefusalReasons.Text(reason),
            Activity.Current?.TraceId.ToString());
        MessagingTelemetry.RecordRefusal(reason);
        return HandlerContinuation.Stop;
    }

    /// <summary>
    /// The resolved message's CLR type name, capped, never <c>Envelope.MessageType</c>: that alias is
    /// text from the sender, which stays untrusted once a transport exists (G3 S-04, T-13).
    /// </summary>
    private static string DescribeMessageType(Envelope envelope)
    {
        var name = envelope.Message?.GetType().FullName ?? "unknown";
        return name.Length <= MaxLoggedTypeNameLength ? name : name[..MaxLoggedTypeNameLength];
    }
}
