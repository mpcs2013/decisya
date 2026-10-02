using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations.Transactions;

/// <summary>#24 ADR-0013 red fixture: starts and enlists transactions, and is not on the ExplicitTransactionRule allow-list.</summary>
public static class UnlistedTransactionUser
{
    public static async Task BeginAsync(DbContext db)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await tx.CommitAsync();
    }

    public static void BeginSync(DbContext db)
    {
        using var tx = db.Database.BeginTransaction();
        tx.Commit();
    }

    public static async Task EnlistAsync(DbContext db, DbTransaction transaction) =>
        await db.Database.UseTransactionAsync(transaction);
}
