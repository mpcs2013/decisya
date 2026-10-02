using Decisya.SharedKernel.Tenancy;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations.UnauditedCommands;

/// <summary>#24 C-1 red fixture: an <c>[AllowCrossTenant]</c> command that never calls <c>IAuditWriter.AppendAsync</c>.</summary>
[AllowCrossTenant("Platform-admin command on an explicit target tenant (ADR-0012). Not audited: must fail CrossTenantAuditRule.")]
public static class UnauditedCrossTenantHandler
{
    public static async Task<int> HandleAsync()
    {
        await Task.Yield();
        return 1;
    }
}
