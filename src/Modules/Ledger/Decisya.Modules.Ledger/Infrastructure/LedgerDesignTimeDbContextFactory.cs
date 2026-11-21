using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Decisya.Modules.Ledger.Infrastructure;

/// <summary>
/// Design-time factory for <c>dotnet ef migrations add</c> / <c>script</c> (issue #341, G2): startup project
/// <c>Decisya.Infrastructure.Migrator</c>. The connection string never dials out (<c>add</c> and <c>script</c>
/// only read the model), so a placeholder host is safe.
/// </summary>
internal sealed class LedgerDesignTimeDbContextFactory : IDesignTimeDbContextFactory<LedgerDbContext>
{
    private const string DesignTimeConnectionString =
        "Host=design-time.invalid;Database=decisya;Username=design-time;Password=design-time";

    public LedgerDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<LedgerDbContext>();
        LedgerDbContextOptions.Configure(builder, DesignTimeConnectionString);

        return new LedgerDbContext(builder.Options, new DesignTimeCurrentTenant());
    }

    private sealed class DesignTimeCurrentTenant : ICurrentTenant
    {
        public TenantResolution Resolution => TenantResolution.Invalid;
    }
}
