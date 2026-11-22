using Microsoft.EntityFrameworkCore;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations.BulkStatements;

/// <summary>
/// #341 G6-341-05 (c) red fixture: two bulk-update call sites in one namespace, so the module has more than the one
/// permitted site. The first stands for the permitted lock statement, the second is the bypass the rule must find.
/// </summary>
public static class TwoUpdateSites
{
    public static Task<int> LockAsync(IQueryable<ScopedProbe> probes) =>
        probes.ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, "locked"));

    public static Task<int> SecondBulkUpdateAsync(IQueryable<ScopedProbe> probes) =>
        probes.ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, "bypassed"));
}

/// <summary>#341 G6-341-05 (c) red fixture: a bulk delete, which no module may use.</summary>
public static class BulkDeleteSite
{
    public static Task<int> PurgeAsync(IQueryable<ScopedProbe> probes) => probes.ExecuteDeleteAsync();
}

/// <summary>#341 G6-341-05 (c) red fixture: a synchronous <c>ExecuteUpdate</c> in a lambda, found as well (no site escapes by form).</summary>
public static class SynchronousLambdaUpdateSite
{
    public static Func<int> Prepare(IQueryable<ScopedProbe> probes) =>
        () => probes.ExecuteUpdate(s => s.SetProperty(p => p.Name, "x"));
}
