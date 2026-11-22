using Microsoft.EntityFrameworkCore;

namespace Decisya.Modules.Ledger.Infrastructure;

/// <summary>
/// The one place <see cref="LedgerDbContext"/>'s <c>UseNpgsql</c> options are built (issue #341, G2): the API
/// registration (<see cref="LedgerModule.AddLedgerModule"/>), the migrator and
/// <see cref="LedgerDesignTimeDbContextFactory"/> all call this, so the migrations history table name and
/// schema can never drift between them. No retrying execution strategy (ADR-0013 point 4: the unit of work is
/// one explicit transaction, and a whole-unit retry would double an audit record).
/// </summary>
public static class LedgerDbContextOptions
{
    public static void Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        builder.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", LedgerDbContext.Schema));
    }
}
