using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations;

/// <summary>
/// G4-22-04 red fixture (T-10b): references <c>IgnoreQueryFilters</c> inside an expression
/// tree (an <c>ldtoken</c> reference to the method, not a <c>call</c>).
/// </summary>
public static class ExpressionTreeCrossTenantQuery
{
    public static Expression<Func<IQueryable<ScopedProbe>, IQueryable<ScopedProbe>>> Reference { get; } =
#pragma warning disable RS0030 // Tenant filter bypass: only in [AllowCrossTenant] types (ADR-0001)
        q => q.IgnoreQueryFilters();
#pragma warning restore RS0030
}
