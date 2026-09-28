using Decisya.Infrastructure.Persistence;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Decisya.ArchitectureTests.Fixtures.TenancyCompliant;

/// <summary>
/// Story 6 scenario 2 green fixture: <c>[AllowCrossTenant]</c> with a real justification may
/// call <c>IgnoreQueryFilters</c>. The call is wrapped in an <c>async</c> method plus a lambda
/// (<c>Select</c>), so <c>CrossTenantQueryRule</c>'s walk to the outermost declaring type must
/// cross at least one compiler-generated boundary (a state machine, a closure) and still credit
/// this type.
/// </summary>
[AllowCrossTenant("reconciliation report for platform support ticket #123")]
public static class AttributedCrossTenantQuery
{
    public static async Task<IReadOnlyList<ScopedEntity>> AllIgnoringFiltersAsync(
        IEnumerable<IQueryable<ScopedEntity>> shards)
    {
        await Task.Yield();

        return shards
            .Select(shard =>
            {
#pragma warning disable RS0030 // Tenant filter bypass: only in [AllowCrossTenant] types (ADR-0001)
                return shard.IgnoreQueryFilters([TenantDbContext.TenantFilterName]);
#pragma warning restore RS0030
            })
            .SelectMany(shard => shard)
            .ToList();
    }
}
