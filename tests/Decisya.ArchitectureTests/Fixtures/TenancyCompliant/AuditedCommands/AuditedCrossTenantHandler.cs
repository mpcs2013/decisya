using System.Data.Common;
using Decisya.Modules.Audit.Contracts;
using Decisya.SharedKernel.Tenancy;

namespace Decisya.ArchitectureTests.Fixtures.TenancyCompliant.AuditedCommands;

/// <summary>#24 C-1 green fixture: an <c>[AllowCrossTenant]</c> command that appends inside an <c>async</c> method (the call lives in the state machine).</summary>
[AllowCrossTenant("Platform-admin command on an explicit target tenant (ADR-0012). Audited through IAuditWriter (ADR-0013).")]
public sealed class AuditedCrossTenantHandler(IAuditWriter audit)
{
    public async Task HandleAsync(TenantId tenantId, DbTransaction transaction)
    {
        await Task.Yield();
        await audit.AppendAsync(new AuditEntry(tenantId, AuditAction.EntitlementsTrialStart, null), transaction);
    }
}
