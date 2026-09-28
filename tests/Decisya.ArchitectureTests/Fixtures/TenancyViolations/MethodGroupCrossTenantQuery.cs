using Microsoft.EntityFrameworkCore;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations;

/// <summary>
/// G4-22-04 red fixture (T-10b): references <c>IgnoreQueryFilters</c> as a method group
/// (an <c>ldftn</c> reference), not a <c>call</c>. A rule that only inspects <c>call</c>
/// instructions misses this.
/// </summary>
public static class MethodGroupCrossTenantQuery
{
#pragma warning disable RS0030 // Tenant filter bypass: only in [AllowCrossTenant] types (ADR-0001)
    public static Func<IQueryable<ScopedProbe>, IQueryable<ScopedProbe>> Reference { get; } =
        EntityFrameworkQueryableExtensions.IgnoreQueryFilters;
#pragma warning restore RS0030
}
