using Microsoft.EntityFrameworkCore;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations;

/// <summary>Story 6 / G4-22-03 red fixture: calls <c>IgnoreQueryFilters</c> without <c>[AllowCrossTenant]</c>.</summary>
public static class UnattributedCrossTenantQuery
{
    public static IQueryable<ScopedProbe> All(IQueryable<ScopedProbe> query)
    {
#pragma warning disable RS0030 // Tenant filter bypass: only in [AllowCrossTenant] types (ADR-0001)
        return query.IgnoreQueryFilters();
#pragma warning restore RS0030
    }
}
