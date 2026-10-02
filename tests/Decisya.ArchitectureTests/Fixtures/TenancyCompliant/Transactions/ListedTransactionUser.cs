using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Decisya.ArchitectureTests.Fixtures.TenancyCompliant.Transactions;

/// <summary>#24 ADR-0013 green fixture: begins a transaction and enlists a caller's one. Passes only when named on the allow-list.</summary>
public static class ListedTransactionUser
{
    public static async Task BeginAsync(DbContext db)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await tx.CommitAsync();
    }

    public static async Task EnlistAsync(DbContext db, DbTransaction transaction) =>
        await db.Database.UseTransactionAsync(transaction);
}
