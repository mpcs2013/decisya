using Microsoft.EntityFrameworkCore;

namespace Decisya.ArchitectureTests.Fixtures.TenancyCompliant.BulkStatements;

/// <summary>
/// #341 G6-341-05 (c) green fixture: exactly one bulk-update call site, named <c>LockAsync</c>, no bulk delete.
/// The setter targets an ordinary column, never <c>TenantId</c>.
/// </summary>
public static class OneUpdateSite
{
    public static Task<int> LockAsync(IQueryable<ScopedEntity> entities) =>
        entities.ExecuteUpdateAsync(s => s.SetProperty(e => e.Name, "locked"));
}
