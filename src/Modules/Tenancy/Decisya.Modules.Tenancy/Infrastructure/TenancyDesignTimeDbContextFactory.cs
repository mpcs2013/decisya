using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore.Design;

namespace Decisya.Modules.Tenancy.Infrastructure;

/// <summary>
/// Design-time factory for <c>dotnet ef migrations add</c> / <c>script</c> (issue #21, G2, D7):
/// startup project <c>Decisya.Infrastructure.Migrator</c>. The connection string never dials
/// out — <c>add</c> and <c>script</c> only read the model — so a placeholder host is safe.
/// </summary>
internal sealed class TenancyDesignTimeDbContextFactory : IDesignTimeDbContextFactory<TenancyDbContext>
{
    private const string DesignTimeConnectionString =
        "Host=design-time.invalid;Database=decisya;Username=design-time;Password=design-time";

    public TenancyDbContext CreateDbContext(string[] args)
    {
        var builder = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<TenancyDbContext>();
        TenancyDbContextOptions.Configure(builder, DesignTimeConnectionString);

        return new TenancyDbContext(builder.Options, new DesignTimeCurrentTenant());
    }

    private sealed class DesignTimeCurrentTenant : ICurrentTenant
    {
        public TenantResolution Resolution => TenantResolution.Invalid;
    }
}
