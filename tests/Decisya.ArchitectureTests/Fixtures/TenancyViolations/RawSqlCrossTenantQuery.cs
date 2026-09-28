using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations;

/// <summary>
/// G4-22-03 red fixture (T-06): raw SQL bypasses the tenant filter entirely. One
/// representative bypass-list member (<c>ExecuteSqlRaw</c>) stands in for the whole raw-SQL
/// group (<c>ExecuteSql</c>, <c>ExecuteSqlAsync</c>, <c>ExecuteSqlInterpolated(Async)</c>,
/// <c>ExecuteSqlRaw(Async)</c>, <c>SqlQuery</c>, <c>SqlQueryRaw</c>, <c>GetDbConnection</c>),
/// which <c>CrossTenantQueryRule</c> matches by declaring type and method name only.
/// </summary>
public static class RawSqlCrossTenantQuery
{
    public static int DeleteEverything(DatabaseFacade database)
    {
#pragma warning disable RS0030 // Tenant filter bypass: only in [AllowCrossTenant] types (ADR-0001)
        return database.ExecuteSqlRaw("DELETE FROM probe.scoped_probes");
#pragma warning restore RS0030
    }
}
