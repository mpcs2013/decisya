using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Decisya.Modules.Audit.Infrastructure;

/// <summary>
/// The one place <see cref="AuditDbContext"/>'s <c>UseNpgsql</c> options are built (issue #24, G2):
/// the migrator, the design-time factory and the tests call <see cref="Configure"/>; the writer calls
/// <see cref="ForConnection"/>. The migrations history table name and schema can never drift.
/// </summary>
public static class AuditDbContextOptions
{
    public static void Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        builder.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", AuditDbContext.Schema));
    }

    /// <summary>
    /// Options over the caller's own, already open connection. The context never owns it (no
    /// <c>contextOwnsConnection</c>): the audited command's context and transaction do.
    /// </summary>
    internal static DbContextOptions<AuditDbContext> ForConnection(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return new DbContextOptionsBuilder<AuditDbContext>().UseNpgsql(connection).Options;
    }
}
