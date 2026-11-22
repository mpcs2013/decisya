using Decisya.ArchitectureTests.Fixtures.TenancyCompliant.BulkStatements;
using Decisya.ArchitectureTests.Fixtures.TenancyViolations.BulkStatements;
using Decisya.Modules.Ledger;

namespace Decisya.ArchitectureTests;

/// <summary>
/// G6-341-05 (c), G3 M341-02 (c), T341-02: the Ledger module has exactly one <c>ExecuteUpdate(Async)</c> call site
/// (the book-row lock in <c>CreateAccountHandler.LockBookAsync</c>) and no <c>ExecuteDelete(Async)</c>. The rule's
/// own detection is proven with the fixtures in <c>Fixtures/TenancyViolations/BulkStatements</c> (red) and
/// <c>Fixtures/TenancyCompliant/BulkStatements</c> (green).
/// </summary>
[Trait("Category", "Architecture")]
public class BulkStatementCallSiteRuleTests
{
    private const string Red = "Decisya.ArchitectureTests.Fixtures.TenancyViolations.BulkStatements";
    private const string Green = "Decisya.ArchitectureTests.Fixtures.TenancyCompliant.BulkStatements";

    [Fact]
    public void The_Ledger_module_has_exactly_one_ExecuteUpdate_call_site_the_book_lock_and_no_ExecuteDelete()
    {
        var outcome = BulkStatementCallSiteRule.Evaluate(BulkStatementCallSiteRule.LedgerLockSite, typeof(LedgerModule).Assembly);

        outcome.IsSuccessful.Should().BeTrue(string.Join("; ", outcome.Violations));
        outcome.UpdateSites.Should().Equal(BulkStatementCallSiteRule.LedgerLockSite);
        outcome.DeleteSites.Should().BeEmpty();
    }

    [Fact]
    public void A_second_ExecuteUpdate_call_site_fails_the_rule()
    {
        var outcome = BulkStatementCallSiteRule.EvaluateIn(Red, $"{Red}.{nameof(TwoUpdateSites)}.{nameof(TwoUpdateSites.LockAsync)}", typeof(TwoUpdateSites).Assembly);

        outcome.IsSuccessful.Should().BeFalse();
        outcome.UpdateSites.Should().Contain(s => s.EndsWith($"{nameof(TwoUpdateSites)}.{nameof(TwoUpdateSites.SecondBulkUpdateAsync)}", StringComparison.Ordinal));
        outcome.Violations.Should().Contain(v => v.Contains("exactly one", StringComparison.Ordinal));
    }

    [Fact]
    public void An_ExecuteDelete_call_site_fails_the_rule()
    {
        var outcome = BulkStatementCallSiteRule.EvaluateIn(Red, $"{Red}.{nameof(TwoUpdateSites)}.{nameof(TwoUpdateSites.LockAsync)}", typeof(BulkDeleteSite).Assembly);

        outcome.DeleteSites.Should().ContainSingle().Which.Should().EndWith($"{nameof(BulkDeleteSite)}.{nameof(BulkDeleteSite.PurgeAsync)}");
        outcome.Violations.Should().Contain(v => v.Contains("bulk delete", StringComparison.Ordinal));
        outcome.IsSuccessful.Should().BeFalse();
    }

    [Fact]
    public void A_synchronous_ExecuteUpdate_inside_a_lambda_is_found_and_named_after_the_method_a_human_wrote()
    {
        var outcome = BulkStatementCallSiteRule.EvaluateIn(Red, "unused", typeof(SynchronousLambdaUpdateSite).Assembly);

        outcome.UpdateSites.Should().Contain($"{Red}.{nameof(SynchronousLambdaUpdateSite)}.{nameof(SynchronousLambdaUpdateSite.Prepare)}");
    }

    [Fact]
    public void A_moved_site_fails_the_rule_even_when_it_is_the_only_one()
    {
        var outcome = BulkStatementCallSiteRule.EvaluateIn(Green, "Decisya.Modules.Ledger.Application.CreateAccountHandler.LockBookAsync", typeof(OneUpdateSite).Assembly);

        outcome.UpdateSites.Should().Equal($"{Green}.{nameof(OneUpdateSite)}.{nameof(OneUpdateSite.LockAsync)}");
        outcome.IsSuccessful.Should().BeFalse("the one site is not the permitted one");
    }

    [Fact]
    public void Exactly_the_one_permitted_site_and_no_delete_passes_the_rule()
    {
        var outcome = BulkStatementCallSiteRule.EvaluateIn(Green, $"{Green}.{nameof(OneUpdateSite)}.{nameof(OneUpdateSite.LockAsync)}", typeof(OneUpdateSite).Assembly);

        outcome.IsSuccessful.Should().BeTrue(string.Join("; ", outcome.Violations));
        outcome.DeleteSites.Should().BeEmpty();
    }

    [Fact]
    public void No_site_at_all_fails_the_rule_so_a_vacuous_scan_cannot_pass()
    {
        var outcome = BulkStatementCallSiteRule.EvaluateIn("Decisya.ArchitectureTests.Fixtures.NoSuchNamespace", "anything", typeof(OneUpdateSite).Assembly);

        outcome.UpdateSites.Should().BeEmpty();
        outcome.IsSuccessful.Should().BeFalse();
    }
}
