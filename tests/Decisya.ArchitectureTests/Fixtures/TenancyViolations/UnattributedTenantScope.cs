using Decisya.SharedKernel.Tenancy;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations;

/// <summary>
/// ADR-0012 / G4-23-01 red fixture: mints a tenant scope with <c>TenantResolution.For</c>
/// inside an <c>async</c> lambda and through a method group, with no <c>[AllowCrossTenant]</c>.
/// </summary>
public static class UnattributedTenantScope
{
    public static Func<TenantId, Task<TenantResolution>> InAsyncLambda { get; } =
        async tenantId =>
        {
            await Task.Yield();
            return TenantResolution.For(tenantId);
        };

    public static Func<TenantId, TenantResolution> AsMethodGroup { get; } = TenantResolution.For;
}
