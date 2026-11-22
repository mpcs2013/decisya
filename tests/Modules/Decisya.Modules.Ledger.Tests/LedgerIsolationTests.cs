using Decisya.Infrastructure.Persistence;
using Decisya.Modules.Ledger.Application;
using Decisya.Modules.Ledger.Domain;
using Decisya.Modules.Ledger.Tests.TestSupport;
using Decisya.SharedKernel;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using static Decisya.Modules.Ledger.Tests.TestSupport.LedgerHarness;

namespace Decisya.Modules.Ledger.Tests;

/// <summary>
/// G1 Story 6, NFR-80, platform invariant 1, G3 M341-01 (a) and (e) (the isolation-test skill): tenant A can neither
/// read, list, rename nor change the currency of tenant B's accounts, by collection or by id; another space's id
/// gives the same not-found outcome as a missing one (the endpoint turns it into the one generic 404); the global
/// query filter and the SaveChanges guard hold; the usage figures are per space. Real Postgres 18, the handlers as
/// the real <c>decisya_ledger</c> role. Test names follow the Gherkin scenario titles.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LedgerIsolationTests(PostgresFixture pg)
{
    private static readonly TenantId TenantA = TenantId.From(Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7"));
    private static readonly TenantId TenantB = TenantId.From(Guid.Parse("2f1a8d5e-3b4c-4e6f-9a7b-1c2d3e4f5a6b"));

    private static async Task<Guid> SeedAsync(LedgerHarness h, TenantId tenant, string name, CancellationToken ct)
    {
        var created = await h.CreateAccountAsync(tenant, ct, name, "EUR", openingMinor: 125000);
        created.Outcome.Should().Be(LedgerOutcome.Succeeded);
        return created.AccountId!.Value;
    }

    [Fact]
    public async Task A_request_for_another_spaces_account_is_indistinguishable_from_a_missing_one()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        var accountOfA = await SeedAsync(h, TenantA, "A's account", ct);
        var missingId = Guid.CreateVersion7();

        // Read by id: B asks for A's id and for an id that does not exist; both are the same null.
        (await h.GetAccountAsync(TenantB, accountOfA, ct)).Should().BeNull();
        (await h.GetAccountAsync(TenantB, missingId, ct)).Should().BeNull();

        var before = await h.AccountsOfAsync(TenantA, ct);
        var auditBefore = await h.AuditRowsOfAsync(TenantA, ct);

        // Rename by id: the same outcome, and the same result value, as for a missing id.
        var foreignRename = await h.RenameAccountAsync(TenantB, accountOfA, "Taken over", ct);
        var missingRename = await h.RenameAccountAsync(TenantB, missingId, "Taken over", ct);
        foreignRename.Outcome.Should().Be(LedgerOutcome.NotFound);
        foreignRename.Should().Be(missingRename);

        // Currency change by id: the same.
        var foreignCurrency = await h.ChangeCurrencyAsync(TenantB, accountOfA, "JPY", null, ct);
        var missingCurrency = await h.ChangeCurrencyAsync(TenantB, missingId, "JPY", null, ct);
        foreignCurrency.Outcome.Should().Be(LedgerOutcome.NotFound);
        foreignCurrency.Should().Be(missingCurrency);

        // Space A's account is unchanged and no audit record was written for either space.
        (await h.AccountsOfAsync(TenantA, ct)).Should().BeEquivalentTo(before);
        (await h.AuditRowsOfAsync(TenantA, ct)).Should().BeEquivalentTo(auditBefore);
        (await h.AuditRowsOfAsync(TenantB, ct)).Should().BeEmpty();
        (await h.AccountsOfAsync(TenantB, ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_list_shows_only_the_callers_own_space()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        var a1 = await SeedAsync(h, TenantA, "A one", ct);
        var a2 = await SeedAsync(h, TenantA, "A two", ct);
        var b1 = await SeedAsync(h, TenantB, "B one", ct);

        var listB = await h.ListAsync(TenantB, ct);
        listB.Accounts.Select(a => a.Id).Should().Equal(b1);
        listB.Usage.Used.Should().Be(1);
        listB.Usage.Limit.Should().Be(AccountLimits.PerSpace);

        var listA = await h.ListAsync(TenantA, ct);
        listA.Accounts.Select(a => a.Id).Should().BeEquivalentTo([a1, a2]);
        listA.Usage.Used.Should().Be(2);
        listA.Usage.Limit.Should().Be(AccountLimits.PerSpace);
    }

    [Fact]
    public async Task Another_spaces_accounts_never_count_against_the_limit_and_creates_never_touch_its_lock_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        await SeedAsync(h, TenantB, "B one", ct);
        await SeedAsync(h, TenantB, "B two", ct);
        (await h.CreateAccountAsync(TenantB, ct)).Outcome.Should().Be(LedgerOutcome.LimitReached);
        var bookB = (await h.BooksOfAsync(TenantB, ct)).Single();

        // Space B is full; space A is empty and may still create 2.
        (await h.CreateAccountAsync(TenantA, ct, "A one")).Outcome.Should().Be(LedgerOutcome.Succeeded);
        (await h.CreateAccountAsync(TenantA, ct, "A two")).Outcome.Should().Be(LedgerOutcome.Succeeded);
        (await h.CreateAccountAsync(TenantA, ct, "A three")).Outcome.Should().Be(LedgerOutcome.LimitReached);

        // The lock statement is filtered to the space's own row: B's book is byte-identical.
        (await h.BooksOfAsync(TenantB, ct)).Single().Should().BeEquivalentTo(bookB);
        (await h.BooksOfAsync(TenantA, ct)).Should().ContainSingle().Which.LockVersion.Should().Be(2);
    }

    [Fact]
    public async Task Tenant_A_cannot_read_tenant_Bs_rows_through_the_global_query_filter()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        var a = await SeedAsync(h, TenantA, "A one", ct);
        var b = await SeedAsync(h, TenantB, "B one", ct);

        await using var contextA = h.Context(TenantA);

        // By a collection query: only A's own rows, accounts and book alike.
        (await contextA.Accounts.Select(x => x.Id).ToListAsync(ct)).Should().Equal(a);
        (await contextA.AccountBooks.Select(x => x.TenantId).ToListAsync(ct)).Should().Equal(TenantA);

        // By tenant B's own id.
        (await contextA.Accounts.SingleOrDefaultAsync(x => x.Id == b, ct)).Should().BeNull();
        (await contextA.Accounts.CountAsync(x => x.Id == b, ct)).Should().Be(0);
    }

    [Fact]
    public async Task Tenant_A_cannot_update_or_delete_tenant_Bs_rows_by_id()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        await SeedAsync(h, TenantA, "A one", ct);
        var b = await SeedAsync(h, TenantB, "B one", ct);
        var accountsBefore = await h.AccountsOfAsync(TenantB, ct);
        var booksBefore = await h.BooksOfAsync(TenantB, ct);

        await using (var contextA = h.Context(TenantA))
        {
            (await contextA.Accounts.Where(x => x.Id == b)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "tampered"), ct)).Should().Be(0);
            (await contextA.Accounts.Where(x => x.Id == b).ExecuteDeleteAsync(ct)).Should().Be(0);
            (await contextA.AccountBooks
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LockVersion, 99L), ct)).Should().Be(1, "only A's own book");
            (await contextA.AccountBooks.ExecuteDeleteAsync(ct)).Should().Be(1, "only A's own book");
        }

        (await h.AccountsOfAsync(TenantB, ct)).Should().BeEquivalentTo(accountsBefore);
        (await h.BooksOfAsync(TenantB, ct)).Should().BeEquivalentTo(booksBefore);
    }

    [Fact]
    public async Task Tenant_A_cannot_save_a_row_that_belongs_to_tenant_B()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        await using (var contextA = h.Context(TenantA))
        {
            contextA.Accounts.Add(Account.Open(TenantB, NewName("Smuggled"), Currency.FromCode("EUR"), null, null, Start));
            var act = () => contextA.SaveChangesAsync(ct);

            var failure = await act.Should().ThrowAsync<TenantIsolationException>();
            failure.Which.Violation.Should().Be(TenantIsolationViolation.TenantMismatch);
        }

        await using (var contextA = h.Context(TenantA))
        {
            contextA.AccountBooks.Add(AccountBook.Open(TenantB, Start));
            var act = () => contextA.SaveChangesAsync(ct);

            var failure = await act.Should().ThrowAsync<TenantIsolationException>();
            failure.Which.Violation.Should().Be(TenantIsolationViolation.TenantMismatch);
        }

        (await h.TotalLedgerRowsAsync(ct)).Should().Be(0);
    }

    [Fact]
    public async Task No_tenant_means_no_rows_and_no_query_reaches_the_ledger_tables()
    {
        var ct = TestContext.Current.CancellationToken;
        var commands = new CommandCountingInterceptor();
        await using var h = await CreateAsync(pg, ct, LedgerFaults.Intercept(commands));
        var accountOfA = await SeedAsync(h, TenantA, "A one", ct);
        var seededCommands = commands.Count;
        var before = await h.AccountsOfAsync(TenantA, ct);

        // "none" is a platform admin without a space; "invalid" is the fail-closed default of an unset resolution.
        foreach (var ambient in new TenantResolution[] { TenantResolution.NoTenant, default })
        {
            (await h.CreateAccountAsync(ambient, ct)).Outcome.Should().Be(LedgerOutcome.Forbidden);
            (await h.RenameAccountAsync(ambient, accountOfA, "Nope", ct)).Outcome.Should().Be(LedgerOutcome.Forbidden);
            (await h.ChangeCurrencyAsync(ambient, accountOfA, "JPY", null, ct)).Outcome.Should().Be(LedgerOutcome.Forbidden);
        }

        commands.Count.Should().Be(seededCommands, "a refused write runs no command against the database");
        (await h.AccountsOfAsync(TenantA, ct)).Should().BeEquivalentTo(before);
        (await h.AuditRowsOfAsync(TenantA, ct)).Should().ContainSingle("only the seeding create is audited");
    }

    [Fact]
    public async Task The_module_persists_only_through_ITenantScoped_aggregates_in_the_ledger_schema()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        await using var db = h.Context(TenantA);

        var entityTypes = db.Model.GetEntityTypes().ToList();

        entityTypes.Should().HaveCount(2);
        entityTypes.Should().OnlyContain(e => typeof(ITenantScoped).IsAssignableFrom(e.ClrType));
        entityTypes.Select(e => e.GetSchema()).Should().OnlyContain(s => s == "ledger");
        entityTypes.Select(e => e.GetTableName()).Should().BeEquivalentTo("accounts", "account_books");
    }
}
