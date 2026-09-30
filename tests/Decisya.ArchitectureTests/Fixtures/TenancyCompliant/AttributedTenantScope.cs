using Decisya.SharedKernel.Tenancy;

namespace Decisya.ArchitectureTests.Fixtures.TenancyCompliant;

/// <summary>ADR-0012 green fixture: an <c>[AllowCrossTenant]</c> type may mint a tenant scope, even inside an async lambda.</summary>
[AllowCrossTenant("Platform-admin command on an explicit target tenant (ADR-0012). Audit: #24.")]
public static class AttributedTenantScope
{
    public static Func<TenantId, Task<TenantResolution>> InAsyncLambda { get; } =
        async tenantId =>
        {
            await Task.Yield();
            return TenantResolution.For(tenantId);
        };
}
