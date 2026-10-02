using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations.Transactions;

/// <summary>#24 G6-24-02 red fixture, listed under the writer's allowance (<c>UseTransaction(Async)</c> only): commits the caller's transaction through EF.</summary>
public static class WriterLikeCommitter
{
    public static async Task EnlistAndCommitAsync(DbContext db, DbTransaction transaction)
    {
        var enlisted = await db.Database.UseTransactionAsync(transaction);
        await enlisted!.CommitAsync();
    }
}

/// <summary>#24 G6-24-02 red fixture, listed under the writer's allowance: rolls the caller's transaction back through EF.</summary>
public static class WriterLikeRoller
{
    public static async Task EnlistAndRollbackAsync(DbContext db, DbTransaction transaction)
    {
        var enlisted = await db.Database.UseTransactionAsync(transaction);
        await enlisted!.RollbackAsync();
    }
}

/// <summary>#24 G6-24-02 red fixture, listed under a handler's allowance: savepoints are nobody's to manage by hand.</summary>
public static class HandlerLikeSavepointUser
{
    public static async Task RunAsync(DbContext db)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await tx.CreateSavepointAsync("manual");
        await tx.CommitAsync();
    }
}
