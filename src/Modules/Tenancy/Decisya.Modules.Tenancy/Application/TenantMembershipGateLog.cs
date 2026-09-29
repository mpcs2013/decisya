using Microsoft.Extensions.Logging;

namespace Decisya.Modules.Tenancy.Application;

/// <summary>
/// The compiled log messages <see cref="TenantMembershipGate"/> writes (CA1848). No message
/// carries the caller's raw <c>sub</c> or the tenant claim value (G2, G3): request-scoped
/// enrichment (identity-dev's <c>CallerContextMiddleware</c>) supplies the hashed user id and
/// the tenant id on every log record instead.
/// </summary>
internal static partial class TenantMembershipGateLog
{
    [LoggerMessage(
        EventId = 2100,
        Level = LogLevel.Information,
        Message = "Tenancy: tenant and Owner membership provisioned on first sign-in.")]
    internal static partial void TenantProvisioned(ILogger logger);

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Warning,
        Message = "Tenancy membership refused: the tenant already exists and the caller has no membership of her own.")]
    internal static partial void MembershipRefused(ILogger logger);

    [LoggerMessage(
        EventId = 2102,
        Level = LogLevel.Warning,
        Message = "Tenancy membership refused after a provisioning race: another caller won the same never-before-seen tenant.")]
    internal static partial void MembershipRefusedAfterRace(ILogger logger);
}
