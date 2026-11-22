using Decisya.Modules.Ledger.Application;
using Decisya.Modules.Ledger.Tests.TestSupport;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.Extensions.DependencyInjection;
using static Decisya.Modules.Ledger.Tests.TestSupport.LedgerHarness;

namespace Decisya.Modules.Ledger.Tests;

/// <summary>
/// G1 Stories 1, 2, 3 and 5 (AC-02-1 to AC-02-4) at the handler level, as the real <c>decisya_ledger</c> role on a
/// migrated Postgres 18 database: the success paths, what is stored (a bigint of minor units and a char(3)
/// currency), the balance and the limit use that the list returns, the third-account refusal, the rename and the
/// currency change with its lock. Test names follow the Gherkin scenario titles. The refusals that need a
/// request (validation, forbidden, generic problem body) are in <c>Decisya.Api.Tests.Authentication.LedgerEndpointPostgresTests</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LedgerAccountScenarioTests(PostgresFixture pg)
{
    private static readonly TenantId Space = TenantId.From(Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7"));

    // ---- Story 1 ----

    [Fact]
    public async Task The_owner_creates_an_account_with_an_opening_balance()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        var created = await h.CreateAccountAsync(Space, ct, "Everyday", "EUR", 125000);

        created.Outcome.Should().Be(LedgerOutcome.Succeeded);
        var account = created.Account!;
        account.Name.Should().Be("Everyday");
        account.Currency.Should().Be("EUR");
        account.OpeningBalance.AmountMinor.Should().Be(125000);
        account.OpeningBalance.Currency.Should().Be("EUR");
        account.OpeningDate.Should().Be("2026-11-01");
        account.Balance.AmountMinor.Should().Be(125000);
        account.CurrencyLocked.Should().BeFalse();

        // The stored amount is a bigint of minor units and the stored currency is char(3) "EUR".
        (await h.QueryAsOwnerAsync(
            "SELECT opening_balance_minor::text || '/' || currency || '/' || opening_date::text FROM ledger.accounts", ct))
            .Should().Equal("125000/EUR/2026-11-01");
        (await h.AuditRowsOfAsync(Space, ct)).Should().ContainSingle();
    }

    [Theory]
    [InlineData("JPY", 5000L)]
    [InlineData("EUR", 125000L)]
    [InlineData("KWD", 1500000L)]
    [InlineData("EUR", -4500L)]
    public async Task Minor_units_follow_the_currencys_own_exponent(string code, long minor)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        var id = (await h.CreateAccountAsync(Space, ct, "Account", code, minor)).AccountId!.Value;

        var read = (await h.GetAccountAsync(Space, id, ct))!;
        read.Balance.AmountMinor.Should().Be(minor, "the balance reads back as exactly the minor units given");
        read.Balance.Currency.Should().Be(code);
        read.OpeningBalance.AmountMinor.Should().Be(minor);
        (await h.QueryAsOwnerAsync("SELECT opening_balance_minor::text FROM ledger.accounts", ct)).Should().Equal(minor.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task The_opening_balance_is_optional()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        var created = await h.CreateAccountAsync(Space, ct, "Savings", "CHF");

        created.Outcome.Should().Be(LedgerOutcome.Succeeded);
        created.Account!.Balance.AmountMinor.Should().Be(0);
        created.Account.Balance.Currency.Should().Be("CHF");
        created.Account.OpeningDate.Should().BeNull();
        (await h.QueryAsOwnerAsync(
            "SELECT opening_balance_minor::text || '/' || currency || '/' || coalesce(opening_date::text, 'none') FROM ledger.accounts", ct))
            .Should().Equal("0/CHF/none");
    }

    // ---- Story 2 ----

    [Fact]
    public async Task The_list_shows_accounts_balances_and_the_limit_use()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        await h.CreateAccountAsync(Space, ct, "Everyday", "EUR", 125000);
        await h.CreateAccountAsync(Space, ct, "Travel", "JPY", 5000);

        var list = await h.ListAsync(Space, ct);

        list.Accounts.Should().HaveCount(2);
        var everyday = list.Accounts.Single(a => a.Name == "Everyday");
        var travel = list.Accounts.Single(a => a.Name == "Travel");
        everyday.Balance.AmountMinor.Should().Be(125000, "each balance is in its own currency, never converted or summed");
        everyday.Balance.Currency.Should().Be("EUR");
        travel.Balance.AmountMinor.Should().Be(5000);
        travel.Balance.Currency.Should().Be("JPY");
        list.Usage.Used.Should().Be(2);
        list.Usage.Limit.Should().Be(2);
    }

    [Fact]
    public async Task An_empty_space_lists_no_account_and_shows_the_limit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        var list = await h.ListAsync(Space, ct);

        list.Accounts.Should().BeEmpty();
        list.Usage.Used.Should().Be(0);
        list.Usage.Limit.Should().Be(2);
    }

    [Fact]
    public async Task The_balance_is_the_opening_balance_plus_the_accounts_transactions()
    {
        var ct = TestContext.Current.CancellationToken;

        // No transaction: the balance is the opening balance.
        await using (var plain = await CreateAsync(pg, ct))
        {
            var id = (await plain.CreateAccountAsync(Space, ct, "Plain", "EUR", 100000)).AccountId!.Value;
            (await plain.GetAccountAsync(Space, id, ct))!.Balance.AmountMinor.Should().Be(100000);
        }

        // Transactions of -2500 and +10000 through the EP-04 seam: 100000 - 2500 + 10000 = 107500.
        await using var withTransactions = await CreateAsync(
            pg, ct, services => services.AddScoped<IAccountActivity, NetActivity>());
        var accountId = (await withTransactions.CreateAccountAsync(Space, ct, "Active", "EUR", 100000)).AccountId!.Value;

        var read = (await withTransactions.GetAccountAsync(Space, accountId, ct))!;
        read.Balance.AmountMinor.Should().Be(107500);
        read.Balance.Currency.Should().Be("EUR");
        read.OpeningBalance.AmountMinor.Should().Be(100000, "the opening balance itself never changes");
        (await withTransactions.ListAsync(Space, ct)).Accounts.Single().Balance.AmountMinor.Should().Be(107500);
    }

    // ---- Story 3 ----

    [Fact]
    public async Task A_third_account_is_refused_and_the_space_still_holds_exactly_2()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        await h.CreateAccountAsync(Space, ct, "One");
        await h.CreateAccountAsync(Space, ct, "Two");
        var auditBefore = (await h.AuditRowsOfAsync(Space, ct)).Count;

        var third = await h.CreateAccountAsync(Space, ct, "Three");

        third.Outcome.Should().Be(LedgerOutcome.LimitReached);
        third.Account.Should().BeNull();
        (await h.AccountsOfAsync(Space, ct)).Should().HaveCount(2);
        (await h.AuditRowsOfAsync(Space, ct)).Should().HaveCount(auditBefore, "no audit record for the refused request");
    }

    // ---- Story 5 ----

    [Fact]
    public async Task The_owner_renames_an_account()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        var id = (await h.CreateAccountAsync(Space, ct, "Everyday", "EUR", 125000)).AccountId!.Value;
        var auditBefore = (await h.AuditRowsOfAsync(Space, ct)).Count;

        var renamed = await h.RenameAccountAsync(Space, id, "Household", ct);

        renamed.Outcome.Should().Be(LedgerOutcome.Succeeded);
        renamed.Account!.Name.Should().Be("Household");
        renamed.Account.Currency.Should().Be("EUR");
        renamed.Account.OpeningBalance.AmountMinor.Should().Be(125000);
        renamed.Account.Balance.AmountMinor.Should().Be(125000);
        (await h.AuditRowsOfAsync(Space, ct)).Should().HaveCount(auditBefore + 1, "exactly one audit record for the rename");
    }

    [Fact]
    public async Task The_currency_can_change_while_the_account_holds_no_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        var id = (await h.CreateAccountAsync(Space, ct, "Everyday", "EUR", 125000)).AccountId!.Value;
        var auditBefore = (await h.AuditRowsOfAsync(Space, ct)).Count;

        var changed = await h.ChangeCurrencyAsync(Space, id, "JPY", 5000, ct);

        changed.Outcome.Should().Be(LedgerOutcome.Succeeded);
        changed.Account!.Currency.Should().Be("JPY");
        changed.Account.OpeningBalance.AmountMinor.Should().Be(5000);
        changed.Account.OpeningBalance.Currency.Should().Be("JPY");
        (await h.QueryAsOwnerAsync("SELECT opening_balance_minor::text || '/' || currency FROM ledger.accounts", ct)).Should().Equal("5000/JPY");
        (await h.AuditRowsOfAsync(Space, ct)).Should().HaveCount(auditBefore + 1, "exactly one audit record");
    }

    [Fact]
    public async Task The_currency_cannot_change_once_the_account_holds_a_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct, LedgerFaults.AccountsHoldATransaction());
        var id = (await h.CreateAccountAsync(Space, ct, "Everyday", "EUR", 125000)).AccountId!.Value;
        var auditBefore = (await h.AuditRowsOfAsync(Space, ct)).Count;

        var changed = await h.ChangeCurrencyAsync(Space, id, "USD", 100, ct);

        changed.Outcome.Should().Be(LedgerOutcome.CurrencyLocked);
        (await h.QueryAsOwnerAsync("SELECT currency FROM ledger.accounts", ct)).Should().Equal("EUR");
        (await h.AuditRowsOfAsync(Space, ct)).Should().HaveCount(auditBefore);
        (await h.GetAccountAsync(Space, id, ct))!.CurrencyLocked.Should().BeTrue();
    }

    [Fact]
    public async Task A_rename_of_a_locked_account_is_still_allowed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct, LedgerFaults.AccountsHoldATransaction());
        var id = (await h.CreateAccountAsync(Space, ct, "Everyday", "EUR", 125000)).AccountId!.Value;

        var renamed = await h.RenameAccountAsync(Space, id, "Renamed", ct);

        renamed.Outcome.Should().Be(LedgerOutcome.Succeeded);
        renamed.Account!.Name.Should().Be("Renamed");
        renamed.Account.Currency.Should().Be("EUR");
    }

    /// <summary>The EP-04 stand-in: every account holds transactions of -2500 and +10000 minor units (net 7500).</summary>
    private sealed class NetActivity : IAccountActivity
    {
        public Task<IReadOnlyDictionary<Guid, AccountActivitySummary>> SummarizeAsync(
            IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken)
        {
            IReadOnlyDictionary<Guid, AccountActivitySummary> summaries =
                accountIds.ToDictionary(id => id, _ => new AccountActivitySummary(-2500 + 10000, false));
            return Task.FromResult(summaries);
        }
    }
}
