using Microsoft.EntityFrameworkCore;

namespace Decisya.Modules.Tenancy.Infrastructure;

/// <summary>
/// The one place <see cref="TenancyDbContext"/>'s <c>UseNpgsql</c> options are built (issue
/// #21, G2): the API registration (<see cref="TenancyModule.AddTenancyModule"/>), the migrator
/// and <see cref="TenancyDesignTimeDbContextFactory"/> all call this, so the migrations
/// history table name and schema can never drift between them.
/// </summary>
public static class TenancyDbContextOptions
{
    public static void Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        builder.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", TenancyDbContext.Schema));
    }
}
