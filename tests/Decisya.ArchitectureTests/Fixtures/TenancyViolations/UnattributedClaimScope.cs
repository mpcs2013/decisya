using Decisya.SharedKernel.Tenancy;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations;

/// <summary>ADR-0012 red fixture: <c>TenantResolution.FromClaim</c> without <c>[AllowCrossTenant]</c>.</summary>
public static class UnattributedClaimScope
{
    public static TenantResolution Mint(string? claim) => TenantResolution.FromClaim(claim);
}
