using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Decisya.ArchitectureTests.Fixtures.TenancyCompliant;

/// <summary>
/// G4-22-05 green fixture: <c>SetProperty</c> targets an ordinary column (<c>Name</c>), never
/// <c>TenantId</c>. <c>TenantId</c> is read only in a separate <c>Where</c> predicate, which is
/// an ordinary, filtered read — not a bulk tenant move.
/// </summary>
public static class SafeBulkUpdateQuery
{
    public static Task<int> RenameAsync(IQueryable<ScopedEntity> probes, TenantId tenantId, string newName) =>
        probes
            .Where(p => p.TenantId == tenantId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, newName));
}
