using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore.Design;

namespace Decisya.Modules.Entitlements.Infrastructure;

/// <summary>
/// Design-time factory for <c>dotnet ef migrations add</c> / <c>script</c> (issue #23, G2, D7):
/// startup project <c>Decisya.Infrastructure.Migrator</c>. The connection string never dials
/// out — <c>add</c> and <c>script</c> only read the model — so a placeholder host is safe.
/// </summary>
internal sealed class EntitlementsDesignTimeDbContextFactory : IDesignTimeDbContextFactory<EntitlementsDbContext>
{
    private const string DesignTimeConnectionString =
        "Host=design-time.invalid;Database=decisya;Username=design-time;Password=design-time";

    public EntitlementsDbContext CreateDbContext(string[] args)
    {
        var builder = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<EntitlementsDbContext>();
        EntitlementsDbContextOptions.Configure(builder, DesignTimeConnectionString);

        return new EntitlementsDbContext(builder.Options, new DesignTimeCurrentTenant());
    }

    private sealed class DesignTimeCurrentTenant : ICurrentTenant
    {
        public TenantResolution Resolution => TenantResolution.Invalid;
    }
}
