using Decisya.SharedKernel.Tenancy;

namespace Decisya.Modules.Entitlements.Infrastructure;

/// <summary>
/// The ambient tenant of one admin command's own context (ADR-0012). It only stores the value:
/// minting it (<c>TenantResolution.For</c>) stays in the <c>[AllowCrossTenant]</c> handler.
/// </summary>
internal sealed class TargetTenant(TenantResolution resolution) : ICurrentTenant
{
    public TenantResolution Resolution { get; } = resolution;
}
