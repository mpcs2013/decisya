namespace Decisya.Modules.Tenancy.Tests.Application;

/// <summary>
/// S-1 (G3 T-07): <c>TenantMembershipGate.EnsureAsync</c> must never run inside an explicit or
/// ambient transaction. The gate itself enforces this with <c>Debug.Assert</c> — deliberately
/// not exercised at runtime here: a failed <c>Debug.Assert</c> calls
/// <see cref="Environment.FailFast(string)"/> in a Debug build with no attached debugger, which
/// would abort the whole test process rather than fail one test (verified empirically in the
/// scratchpad before writing this file). A source-level check is the safe way to pin S-1's
/// guard without risking the test host.
/// </summary>
[Trait("Category", "Unit")]
public class TenantMembershipGateTransactionGuardTests
{
    private static readonly string SourcePath = RepoPaths.Find(
        Path.Combine("src", "Modules", "Tenancy", "Decisya.Modules.Tenancy", "Application", "TenantMembershipGate.cs"));

    [Fact]
    public void EnsureAsync_asserts_it_never_runs_inside_a_transaction()
    {
        var content = File.ReadAllText(SourcePath);

        content.Should().Contain(
            "Debug.Assert(db.Database.CurrentTransaction is null",
            "S-1 (T-07): the gate must assert it never runs inside an explicit or ambient transaction");
    }

    [Fact]
    public void EnsureAsync_never_opens_its_own_transaction()
    {
        var content = File.ReadAllText(SourcePath);

        content.Should().NotContain("BeginTransaction");
        content.Should().NotContain("UseTransaction");
    }
}
