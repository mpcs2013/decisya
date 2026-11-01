namespace Decisya.ArchitectureTests.Fixtures.TenancyCompliant.MessageHandlers;

// R5 / Story 6 scenario 2 green fixture (#231): a module that lists a handler declared in its own assembly,
// with no [AllowCrossTenant], and whose two-tenant isolation test the tests supply.

/// <summary>A handler with an isolation test (the tests build the file in memory).</summary>
public static class TestedLedgerHandler
{
    public static void Handle(object message) => ArgumentNullException.ThrowIfNull(message);
}

/// <summary>Lists <see cref="TestedLedgerHandler"/>.</summary>
public static class CompliantHandlerModule
{
    public static IReadOnlyList<Type> MessageHandlerTypes { get; } = [typeof(TestedLedgerHandler)];
}
