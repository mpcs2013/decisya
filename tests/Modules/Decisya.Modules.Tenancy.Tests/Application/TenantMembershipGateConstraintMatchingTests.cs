using System.Reflection;
using Decisya.Modules.Tenancy.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Decisya.Modules.Tenancy.Tests.Application;

/// <summary>
/// G4-21-03: "The catch matches <c>PostgresException.SqlState == "23505"</c> <b>and</b>
/// <c>ConstraintName</c> in {the <c>tenants</c> PK, <c>ux_memberships_tenant_user</c>}. Any
/// other exception propagates as the generic 500." <c>IsRaceLossOnProvisioning</c> is
/// <c>private static</c>, pure and deterministic (no database access), so it is exercised here
/// directly by reflection rather than through a real, hard-to-reproduce Postgres race (see
/// <see cref="TenantMembershipGateRaceTests"/> for the real-database race proof of the two
/// reachable branches).
/// </summary>
[Trait("Category", "Unit")]
public class TenantMembershipGateConstraintMatchingTests
{
    private static readonly MethodInfo IsRaceLossOnProvisioning = typeof(TenantMembershipGate)
        .GetMethod("IsRaceLossOnProvisioning", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("TenantMembershipGate.IsRaceLossOnProvisioning was not found by reflection.");

    [Theory]
    [InlineData("PK_tenants", true)]
    [InlineData("ux_memberships_tenant_user", true)]
    [InlineData("some_other_unique_constraint", false)]
    [InlineData(null, false)]
    public void SqlState_23505_is_handled_only_on_the_two_named_constraints(string? constraintName, bool expectRaceLoss)
    {
        var postgresException = new PostgresException("duplicate key value", "ERROR", "ERROR", "23505", constraintName: constraintName);
        var dbUpdateException = new DbUpdateException("conflict", postgresException);

        var result = Invoke(dbUpdateException);

        result.Should().Be(expectRaceLoss);
    }

    [Fact]
    public void A_non_23505_SqlState_is_never_treated_as_a_provisioning_race_loss_even_on_a_named_constraint()
    {
        var postgresException = new PostgresException("serialization failure", "ERROR", "ERROR", "40001", constraintName: "PK_tenants");
        var dbUpdateException = new DbUpdateException("conflict", postgresException);

        Invoke(dbUpdateException).Should().BeFalse();
    }

    [Fact]
    public void A_DbUpdateException_with_no_PostgresException_inner_is_never_treated_as_a_provisioning_race_loss()
    {
        var dbUpdateException = new DbUpdateException("conflict", new InvalidOperationException("not Postgres"));

        Invoke(dbUpdateException).Should().BeFalse();
    }

    private static bool Invoke(DbUpdateException exception) =>
        (bool)IsRaceLossOnProvisioning.Invoke(null, [exception])!;
}
