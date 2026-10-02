using Decisya.SharedKernel.Tenancy;
using NodaTime;

namespace Decisya.Modules.Entitlements.Contracts.Admin;

/// <summary>
/// The three cross-tenant platform-admin commands of the Entitlements module, as one public
/// surface for <c>Decisya.Modules.Admin</c> (issue #25, G2; ADR-0012 amendment 1). Registered
/// scoped. The implementation only delegates to the module's internal
/// <c>[AllowCrossTenant]</c> handlers, which check the caller (<c>None</c> and
/// <c>ICurrentCaller.IsPlatformAdmin</c>), validate, check that the target tenant exists, and
/// write the change and its audit record in one transaction (ADR-0013).
/// </summary>
/// <remarks>
/// <para>
/// This is the only namespace of <c>Decisya.Modules.Entitlements.Contracts</c> whose members may
/// name a <see cref="TenantId"/>: an admin command acts on an explicit target tenant.
/// <c>IEntitlementService</c> still never takes one. A NetArchTest rule allows only
/// <c>Decisya.Modules.Admin</c> and <c>Decisya.Modules.Entitlements</c> to depend on this
/// namespace.
/// </para>
/// <para>
/// Every method returns an <see cref="EntitlementAdminResult"/> for an expected outcome and
/// throws for anything else (a database failure included), which the host turns into a generic
/// 500. A refusal is never turned into a success.
/// </para>
/// </remarks>
public interface IEntitlementAdminCommands
{
    /// <summary>Starts the target tenant's one 14-day Pro trial.</summary>
    Task<EntitlementAdminResult> StartTrialAsync(TenantId tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Grants, or replaces, one feature override for the target tenant. <paramref name="reason"/>
    /// is free text and sensitive: the module never logs it, never puts it in an error and never
    /// writes it to the audit record. <paramref name="expiresAt"/> <see langword="null"/> means
    /// "until revoked".
    /// </summary>
    Task<EntitlementAdminResult> GrantOverrideAsync(
        TenantId tenantId, FeatureKey feature, string reason, Instant? expiresAt, CancellationToken cancellationToken = default);

    /// <summary>Revokes the target tenant's override for one feature. Revoking a missing override succeeds.</summary>
    Task<EntitlementAdminResult> RevokeOverrideAsync(TenantId tenantId, FeatureKey feature, CancellationToken cancellationToken = default);
}
