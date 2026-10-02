using Decisya.SharedKernel.Tenancy;

namespace Decisya.Modules.Audit.Infrastructure;

/// <summary>
/// The ambient tenant of one audit append's own context: the entry's target tenant. It only
/// stores the value; minting it (<c>TenantResolution.For</c>) stays in the <c>[AllowCrossTenant]</c>
/// writer. Module-internal copy of the Entitlements type (ADR-0005: no cross-module implementation reference).
/// </summary>
internal sealed class TargetTenant(TenantResolution resolution) : ICurrentTenant
{
    public TenantResolution Resolution { get; } = resolution;
}
