using Microsoft.EntityFrameworkCore;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations.Transactions;

/// <summary>#24 ADR-0013 red fixture: turns the auto-savepoints off. No type may, allow-listed or not.</summary>
public static class SavepointsDisabled
{
    public static void Turn(DbContext db) => db.Database.AutoSavepointsEnabled = false;
}

/// <summary>#24 ADR-0013 red fixture: switches off EF's automatic transactions.</summary>
public static class AutoTransactionsDisabled
{
    public static void Turn(DbContext db) => db.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;
}
