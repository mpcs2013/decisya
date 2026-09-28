using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations;

/// <summary>Story 6 scenario 3 red fixture: <c>[AllowCrossTenant]</c> with a blank justification.</summary>
[AllowCrossTenant(" ")]
public static class BlankJustificationQuery
{
    public static IQueryable<ScopedProbe> All(IQueryable<ScopedProbe> query)
    {
#pragma warning disable RS0030 // Tenant filter bypass: only in [AllowCrossTenant] types (ADR-0001)
        return query.IgnoreQueryFilters();
#pragma warning restore RS0030
    }
}
