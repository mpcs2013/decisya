using Decisya.Modules.Ledger.Application;
using Decisya.Modules.Ledger.Domain;
using Decisya.Modules.Ledger.Tests.TestSupport;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Npgsql;

namespace Decisya.Modules.Ledger.Tests;

/// <summary>
/// G1 Story 4 (AC-02-5, NFR-81) and G3 M341-02 (d): 16 creates start together behind a gate, each with its own
/// DI scope and context, as the real <c>decisya_ledger</c> role on a migrated Postgres 18 database, from 0, 1 and 2
/// existing accounts, for 20 rounds on fresh spaces each. The space never holds more than
/// <see cref="AccountLimits.PerSpace"/> accounts, every surplus create gets the limit refusal, and no create is lost,
/// duplicated or ends in an exception. Test names are the Gherkin scenario titles.
/// </summary>
/// <remarks>
/// How the tests prove the row lock (G2 D2). Without the book-row <c>UPDATE</c> the 16 counts would all run against
/// the same snapshot, so a round would end with more than 2 accounts (and more successes than <c>2 - existing</c>),
/// and <c>lock_version</c> would stay at 0 instead of growing by one per committed create. Both assertions fail
/// the round. The lock-wait test is the deterministic half: it holds the space's lock row from another connection
/// and shows a create does not complete, then sees the account the holder committed.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class LedgerAccountLimitRaceTests(PostgresFixture pg)
{
    private const int ParallelCreates = 16;
    private const int Rounds = 20;

    private readonly record struct Attempt(LedgerOutcome? Outcome, Guid? AccountId, string? Failure);

    [Fact]
    public async Task Parallel_creates_never_pass_the_limit_from_empty()
    {
        await RunScenarioAsync(existing: 0, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Parallel_creates_never_pass_the_limit_from_one_account()
    {
        await RunScenarioAsync(existing: 1, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Parallel_creates_at_the_limit_stay_at_the_limit()
    {
        await RunScenarioAsync(existing: 2, TestContext.Current.CancellationToken);
    }

    private async Task RunScenarioAsync(int existing, CancellationToken ct)
    {
        var allowed = AccountLimits.PerSpace - existing;
        await using var h = await LedgerHarness.CreateAsync(pg, ct);
        await h.WarmPoolAsync(ParallelCreates, ct);

        // "The race test repeats": each scenario is run for 20 rounds on fresh spaces.
        for (var round = 1; round <= Rounds; round++)
        {
            var tenant = TenantId.New();
            var seedIds = new List<Guid>();
            for (var i = 0; i < existing; i++)
            {
                var seeded = await h.CreateAccountAsync(tenant, ct, $"Seed {i}");
                seeded.Outcome.Should().Be(LedgerOutcome.Succeeded, $"round {round}: seeding");
                seedIds.Add(seeded.AccountId!.Value);
            }

            var lockBefore = existing == 0 ? 0 : (await h.BooksOfAsync(tenant, ct)).Single().LockVersion;
            var auditBefore = (await h.AuditRowsOfAsync(tenant, ct)).Count;

            var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = Enumerable.Range(0, ParallelCreates)
                .Select(i => AttemptAsync(h, tenant, go.Task, i, ct))
                .ToList();
            go.SetResult();
            var results = await Task.WhenAll(attempts);

            var context = $"round {round}, {existing} existing";

            // No request ends in an exception (the 5xx of the HTTP half is proven in Decisya.Api.Tests).
            results.Where(r => r.Failure is not null).Select(r => r.Failure).Should().BeEmpty(context);

            // Exactly 2 - existing succeed; every surplus create is refused as "limit reached"; none is lost.
            results.Count(r => r.Outcome == LedgerOutcome.Succeeded).Should().Be(allowed, context);
            results.Count(r => r.Outcome == LedgerOutcome.LimitReached).Should().Be(ParallelCreates - allowed, context);

            // The space holds exactly the limit, and every successful create is one of the stored accounts, once.
            var stored = (await h.AccountsOfAsync(tenant, ct)).Select(a => a.Id).ToList();
            stored.Should().HaveCount(AccountLimits.PerSpace, context);
            stored.Should().OnlyHaveUniqueItems(context);
            var succeededIds = results.Where(r => r.Outcome == LedgerOutcome.Succeeded).Select(r => r.AccountId!.Value).ToList();
            succeededIds.Should().OnlyHaveUniqueItems(context);
            stored.Should().BeEquivalentTo(seedIds.Concat(succeededIds), context);

            // Exactly one ledger audit record per account, one per successful create, none for a refusal.
            var audit = await h.AuditRowsOfAsync(tenant, ct);
            audit.Should().HaveCount(auditBefore + allowed, context);
            audit.Should().OnlyContain(r => r.Action == "ledger.account.create", context);
            audit.Select(r => r.SubjectId).Should().BeEquivalentTo(stored.Select(id => (Guid?)id), context);

            // The lock row: one book per space, incremented once per committed create. A refused create's
            // increment is rolled back, so an all-refused round leaves lock_version unchanged.
            var book = (await h.BooksOfAsync(tenant, ct)).Should().ContainSingle(context).Which;
            book.LockVersion.Should().Be(lockBefore + allowed, context);
            if (allowed == 0)
            {
                book.LockVersion.Should().Be(lockBefore, $"{context}: an all-refused round leaves lock_version unchanged");
            }
        }
    }

    private static async Task<Attempt> AttemptAsync(
        LedgerHarness h, TenantId tenant, Task gate, int index, CancellationToken ct)
    {
        await gate;
        try
        {
            var result = await h.CreateAccountAsync(tenant, ct, $"Parallel {index}");
            return new Attempt(result.Outcome, result.AccountId, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new Attempt(null, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    [Fact]
    public async Task A_create_waits_for_the_spaces_lock_row_then_sees_the_account_the_holder_committed_while_another_space_does_not_wait()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await LedgerHarness.CreateAsync(pg, ct);

        var tenant = TenantId.New();
        var otherTenant = TenantId.New();
        (await h.CreateAccountAsync(tenant, ct, "First")).Outcome.Should().Be(LedgerOutcome.Succeeded);

        // The holder takes the same row lock the create takes (the book's UPDATE) and adds the second account.
        await using var holder = new NpgsqlConnection(h.OwnerConnectionString);
        await holder.OpenAsync(ct);
        await using var holderTransaction = await holder.BeginTransactionAsync(ct);
        await using (var lockStatement = new NpgsqlCommand(
            "UPDATE ledger.account_books SET lock_version = lock_version + 1 WHERE tenant_id = @tenant", holder, holderTransaction))
        {
            lockStatement.Parameters.AddWithValue("tenant", tenant.Value);
            (await lockStatement.ExecuteNonQueryAsync(ct)).Should().Be(1);
        }

        await LedgerHarness.InsertRawAccountAsync(holder, holderTransaction, tenant, ct);

        var waiting = h.CreateAccountAsync(tenant, ct, "Second");

        // If the create did not take the lock it would count 1 and succeed at once, which fails this assertion.
        var finished = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(2), ct));
        ReferenceEquals(finished, waiting).Should().BeFalse("a create in the same space waits for the lock row");

        // A create in another space does not wait.
        var other = await h.CreateAccountAsync(otherTenant, ct, "Other").WaitAsync(TimeSpan.FromSeconds(20), ct);
        other.Outcome.Should().Be(LedgerOutcome.Succeeded);

        await holderTransaction.CommitAsync(ct);

        // After the grant the count sees the holder's account: 2 of 2, so the waiting create is refused.
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(30), ct);
        result.Outcome.Should().Be(LedgerOutcome.LimitReached);
        (await h.AccountsOfAsync(tenant, ct)).Should().HaveCount(AccountLimits.PerSpace);
        (await h.AccountsOfAsync(otherTenant, ct)).Should().ContainSingle();
    }
}
