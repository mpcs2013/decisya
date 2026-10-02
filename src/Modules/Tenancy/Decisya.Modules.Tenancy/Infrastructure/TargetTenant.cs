using Decisya.SharedKernel.Tenancy;

namespace Decisya.Modules.Tenancy.Infrastructure;

/// <summary>
/// The ambient tenant of <see cref="Application.TenantExistence"/>'s own context (issue #25, G2 D4;
/// ADR-0012 amendment 1 point 6). It only stores the value: the resolution is minted by the
/// <c>[AllowCrossTenant]</c> caller and handed across as a parameter, never created here.
/// </summary>
internal sealed class TargetTenant(TenantResolution resolution) : ICurrentTenant
{
    public TenantResolution Resolution { get; } = resolution;
}
