using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations;

/// <summary>
/// G4-22-03 red fixture (T-07): <c>EntityEntry.GetDatabaseValuesAsync</c> ignores query filters
/// internally, so a conflict handler that calls it can read another tenant's row back. One
/// representative bypass-list member stands in for the whole group (<c>GetDatabaseValues</c>,
/// <c>GetDatabaseValuesAsync</c>, <c>Reload</c>, <c>ReloadAsync</c>).
/// </summary>
public static class EntryReloadCrossTenantQuery
{
    public static async Task<PropertyValues?> ReadBackAsync(EntityEntry<ScopedProbe> entry)
    {
#pragma warning disable RS0030 // Tenant filter bypass: only in [AllowCrossTenant] types (ADR-0001)
        return await entry.GetDatabaseValuesAsync();
#pragma warning restore RS0030
    }
}
