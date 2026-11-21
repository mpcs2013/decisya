using System.Data;
using Microsoft.EntityFrameworkCore;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations.Transactions;

/// <summary>#341 G2 R-L3 red fixture: begins a transaction with an explicit <see cref="IsolationLevel"/>.</summary>
public static class IsolationLevelTransactionUser
{
    public static async Task BeginSerializableAsync(DbContext db)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await tx.CommitAsync();
    }

    public static void BeginSerializableSync(DbContext db)
    {
        using var tx = db.Database.BeginTransaction(IsolationLevel.Serializable);
        tx.Commit();
    }
}
