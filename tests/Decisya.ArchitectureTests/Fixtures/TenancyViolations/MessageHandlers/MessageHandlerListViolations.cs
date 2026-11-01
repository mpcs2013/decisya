using Decisya.Infrastructure.Persistence;
using Decisya.SharedKernel.Tenancy;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations.MessageHandlers;

// R5 / G4-231-03 / Story 6 scenario 2 red fixtures (#231): a module exposes MessageHandlerTypes the way the
// architecture note describes ("Module handler surface"), and each module breaks one clause of
// MessageHandlerListRule.

/// <summary>A plain handler that no two-tenant isolation test names.</summary>
public static class UntestedLedgerHandler
{
    public static void Handle(object message) => ArgumentNullException.ThrowIfNull(message);
}

/// <summary>A cross-tenant handler: the HTTP admin role and MFA policy do not apply to a message.</summary>
[AllowCrossTenant("fixture: a cross-tenant handler must never be listed as a message handler")]
public static class CrossTenantLedgerHandler
{
    public static void Handle(object message) => ArgumentNullException.ThrowIfNull(message);
}

/// <summary>Lists <see cref="UntestedLedgerHandler"/>, which has no isolation test (Story 6 scenario 2).</summary>
public static class UntestedHandlerModule
{
    public static IReadOnlyList<Type> MessageHandlerTypes { get; } = [typeof(UntestedLedgerHandler)];
}

/// <summary>Lists <see cref="CrossTenantLedgerHandler"/>, which carries <c>[AllowCrossTenant]</c> (G4-231-03).</summary>
public static class CrossTenantHandlerModule
{
    public static IReadOnlyList<Type> MessageHandlerTypes { get; } = [typeof(CrossTenantLedgerHandler)];
}

/// <summary>Lists a type declared in another assembly (G1 Story 3 scenario 3: a module cannot register another module's handler).</summary>
public static class ForeignHandlerModule
{
    public static IReadOnlyList<Type> MessageHandlerTypes { get; } = [typeof(TenantDbContext)];
}
