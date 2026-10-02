using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Decisya.ArchitectureTests.Fixtures.TenancyCompliant.RawAdoNet;

/// <summary>#24 G4-24-03 green fixture: the writer's whole use of the transaction. Reads <c>.Connection</c>, builds a context on it and passes the transaction to <c>UseTransactionAsync</c>.</summary>
public static class EnlistOnly
{
    public static async Task AppendAsync(DbTransaction transaction, CancellationToken cancellationToken)
    {
        var connection = transaction.Connection ?? throw new InvalidOperationException("completed");
        var options = new DbContextOptionsBuilder<DbContext>().UseNpgsql(connection).Options;
        await using var db = new DbContext(options);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }
}
