using Microsoft.EntityFrameworkCore;

namespace Decisya.Modules.Entitlements.Infrastructure;

/// <summary>
/// The one place <see cref="EntitlementsDbContext"/>'s <c>UseNpgsql</c> options are built (issue
/// #23, G2): the API registration (<see cref="EntitlementsModule.AddEntitlementsModule"/>), the migrator
/// and <see cref="EntitlementsDesignTimeDbContextFactory"/> all call this, so the migrations
/// history table name and schema can never drift between them.
/// </summary>
public static class EntitlementsDbContextOptions
{
    public static void Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        builder.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", EntitlementsDbContext.Schema));
    }
}
