using Decisya.SharedKernel.Tenancy;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations;

/// <summary>
/// G3 S-1 red fixture: mints <c>TenantResolution.NoTenant</c>, the resolution that satisfies
/// an admin command's precondition, without <c>[AllowCrossTenant]</c>.
/// </summary>
public static class UnattributedNoTenantMint
{
    public static TenantResolution Mint() => TenantResolution.NoTenant;
}
