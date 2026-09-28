using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations;

/// <summary>
/// G4-22-05 red fixture (T-08): <c>ExecuteUpdate</c> setting <c>TenantId</c> moves the
/// caller's own (filtered) rows into another tenant without <c>SaveChanges</c> ever seeing it.
/// <c>[AllowCrossTenant]</c> never exempts this — it is a separate, always-on rule.
/// </summary>
public static class BulkTenantMoveQuery
{
    public static Task<int> MoveAsync(IQueryable<ScopedProbe> probes, TenantId newTenantId) =>
        probes.ExecuteUpdateAsync(s => s.SetProperty(p => p.TenantId, newTenantId));
}
