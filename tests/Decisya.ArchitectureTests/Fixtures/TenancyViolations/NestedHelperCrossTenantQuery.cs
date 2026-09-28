using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations;

/// <summary>
/// G4-22-04 red fixture (T-10a): a user-declared nested type inside an attributed outer type
/// must not inherit the outer type's <c>[AllowCrossTenant]</c> permission — only a
/// compiler-generated nested type (a closure, an async state machine) does. The rule must
/// name <c>NestedHelperCrossTenantQuery+Helper</c>, not the (correctly attributed) outer type.
/// </summary>
[AllowCrossTenant("reconciliation report for platform support ticket #123")]
public static class NestedHelperCrossTenantQuery
{
    public static class Helper
    {
        public static IQueryable<ScopedProbe> All(IQueryable<ScopedProbe> query)
        {
#pragma warning disable RS0030 // Tenant filter bypass: only in [AllowCrossTenant] types (ADR-0001)
            return query.IgnoreQueryFilters();
#pragma warning restore RS0030
        }
    }
}
