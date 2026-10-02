using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Decisya.Modules.Audit.Infrastructure;

/// <summary>
/// Design-time factory for <c>dotnet ef migrations add</c> / <c>script</c> (issue #24, G2):
/// startup project <c>Decisya.Infrastructure.Migrator</c>. The connection string never dials out,
/// so a placeholder host is safe.
/// </summary>
internal sealed class AuditDesignTimeDbContextFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    private const string DesignTimeConnectionString =
        "Host=design-time.invalid;Database=decisya;Username=design-time;Password=design-time";

    public AuditDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<AuditDbContext>();
        AuditDbContextOptions.Configure(builder, DesignTimeConnectionString);

        return new AuditDbContext(builder.Options, new DesignTimeCurrentTenant());
    }

    private sealed class DesignTimeCurrentTenant : ICurrentTenant
    {
        public TenantResolution Resolution => TenantResolution.Invalid;
    }
}
