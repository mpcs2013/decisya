using System.Diagnostics;
using Decisya.Modules.Ledger.Application;
using Decisya.Modules.Ledger.Tests.TestSupport;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static Decisya.Modules.Ledger.Tests.TestSupport.LedgerHarness;

namespace Decisya.Modules.Ledger.Tests;

/// <summary>
/// G1 Story 7, NFR-83 and G3 M341-03 (b) and (d) (ADR-0013 amendment 1), end to end as the real
/// <c>decisya_ledger</c> role on a migrated Postgres 18 database: each successful write leaves exactly one
/// audit record of the right ledger action with the account id as subject and no name or amount; the account
/// write and its record commit together or not at all; a refusal leaves none; and the database refuses a malformed
/// ledger audit row inserted with raw SQL, which is the layer that holds if the writer is bypassed.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LedgerAuditTests(PostgresFixture pg)
{
    // Run-time canaries (testcontainers addendum): the name and the amount must never reach an audit column.
    private static readonly string NameCanary = "MARKER-" + Guid.NewGuid().ToString("N")[..8];
    private static readonly long AmountCanary = 918_273_645;

    private static readonly TenantId TenantA = TenantId.From(Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7"));
    private static readonly TenantId TenantB = TenantId.From(Guid.Parse("2f1a8d5e-3b4c-4e6f-9a7b-1c2d3e4f5a6b"));

    // ---- Story 7, scenario 1 ----

    [Fact]
    public async Task A_create_a_rename_and_a_currency_change_each_write_one_audit_record_in_the_same_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        // An ambient activity gives the writer a trace id to record, as a request would.
        using var activity = new Activity("ledger-audit-test").Start();
        var expectedTrace = activity.TraceId.ToHexString();

        var created = await h.CreateAccountAsync(TenantA, ct, NameCanary, "EUR", AmountCanary);
        created.Outcome.Should().Be(LedgerOutcome.Succeeded);
        var accountId = created.AccountId!.Value;
        (await h.AuditRowsOfAsync(TenantA, ct)).Should().ContainSingle("one record per write, written with the account");

        (await h.RenameAccountAsync(TenantA, accountId, NameCanary + " renamed", ct)).Outcome.Should().Be(LedgerOutcome.Succeeded);
        (await h.AuditRowsOfAsync(TenantA, ct)).Should().HaveCount(2);

        (await h.ChangeCurrencyAsync(TenantA, accountId, "JPY", AmountCanary, ct)).Outcome.Should().Be(LedgerOutcome.Succeeded);

        var rows = await h.AuditRowsOfAsync(TenantA, ct);
        rows.Should().HaveCount(3, "exactly one record per write");
        rows.Select(r => r.Action).Should().BeEquivalentTo(
            ["ledger.account.create", "ledger.account.rename", "ledger.account.currency_change"]);
        rows.Should().OnlyContain(r => r.SubjectId == accountId, "the subject is the account id");
        rows.Should().OnlyContain(r => r.TenantId == TenantA.Value);
        rows.Should().OnlyContain(r => r.ActorUserId == TestCurrentCaller.DefaultUserId, "the actor is the validated caller id");
        rows.Should().OnlyContain(r => r.OccurredAt == Start, "the time comes from IClock");
        rows.Should().OnlyContain(r => r.TraceId == expectedTrace, "the trace id is the ambient activity's");
        rows.Should().OnlyContain(r => r.Outcome == "succeeded");
        rows.Should().OnlyContain(r => r.FeatureKey == null, "a ledger action carries no feature key");

        // None carries the account name or an amount: scan every column of every record, any tenant.
        var texts = await h.AuditRowTextsAsync(ct);
        texts.Should().NotBeEmpty();
        texts.Should().NotContain(t => t.Contains(NameCanary, StringComparison.Ordinal), "no account name in the audit record");
        texts.Should().NotContain(
            t => t.Contains(AmountCanary.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
            "no amount in the audit record");
    }

    [Fact]
    public async Task Audit_records_are_per_space_and_a_write_in_one_space_leaves_none_in_another()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        var created = await h.CreateAccountAsync(TenantA, ct);
        (await h.RenameAccountAsync(TenantA, created.AccountId!.Value, "Renamed", ct)).Outcome.Should().Be(LedgerOutcome.Succeeded);

        (await h.AuditRowsOfAsync(TenantA, ct)).Should().HaveCount(2);
        (await h.AuditRowsOfAsync(TenantB, ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Two_simultaneous_renames_both_succeed_and_each_leaves_one_audit_record()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        var accountId = (await h.CreateAccountAsync(TenantA, ct)).AccountId!.Value;
        await h.WarmPoolAsync(2, ct);

        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<LedgerOutcome> RenameAsync(string name)
        {
            await go.Task;
            return (await h.RenameAccountAsync(TenantA, accountId, name, ct)).Outcome;
        }

        var first = RenameAsync("First writer");
        var second = RenameAsync("Second writer");
        go.SetResult();
        var outcomes = await Task.WhenAll(first, second);

        outcomes.Should().OnlyContain(o => o == LedgerOutcome.Succeeded, "the last writer wins and neither is refused");
        (await h.AuditRowsOfAsync(TenantA, ct)).Where(r => r.Action == "ledger.account.rename").Should().HaveCount(2);
        (await h.AccountsOfAsync(TenantA, ct)).Single().Name.Should().BeOneOf("First writer", "Second writer");
    }

    // ---- Story 7, scenario 2: a failed audit write rolls the account write back ----

    [Fact]
    public async Task A_failed_audit_write_rolls_the_account_write_back_for_a_create()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct, LedgerFaults.Decorate(inner => new ThrowAfterAppendWriter(inner)));

        var act = () => h.CreateAccountAsync(TenantA, ct);

        await act.Should().ThrowAsync<InjectedFaultException>("the request does not report success");
        (await h.TotalLedgerRowsAsync(ct)).Should().Be(0, "no account and no book row survive the rollback");
        (await h.AuditRowTextsAsync(ct)).Should().BeEmpty("the real append ran, and rolled back with the rest");
    }

    [Fact]
    public async Task An_audit_append_the_database_refuses_rolls_the_account_write_back_for_a_create()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        // A dedicated database: the role loses its one audit privilege, as a failed grant would leave it.
        await h.ExecuteAsOwnerAsync($"REVOKE INSERT ON audit.audit_records FROM {LedgerModule.DatabaseRole}", ct);

        var act = () => h.CreateAccountAsync(TenantA, ct);

        var failure = await act.Should().ThrowAsync<DbUpdateException>();
        failure.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await h.TotalLedgerRowsAsync(ct)).Should().Be(0);
    }

    [Fact]
    public async Task A_commit_that_fails_after_the_audit_append_leaves_no_account_and_no_record_for_a_create()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct, LedgerFaults.Intercept(new ThrowOnCommitInterceptor()));

        var act = () => h.CreateAccountAsync(TenantA, ct);

        await act.Should().ThrowAsync<InjectedFaultException>();
        (await h.TotalLedgerRowsAsync(ct)).Should().Be(0);
        (await h.AuditRowTextsAsync(ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_audit_write_rolls_the_rename_back_and_a_failed_currency_change_too()
    {
        var ct = TestContext.Current.CancellationToken;
        var armed = new ThrowOnCommitInterceptor(armed: false);
        await using var h = await CreateAsync(pg, ct, LedgerFaults.Intercept(armed));
        var accountId = (await h.CreateAccountAsync(TenantA, ct, "Before", "EUR", 1000)).AccountId!.Value;
        var before = await h.AccountsOfAsync(TenantA, ct);

        armed.Armed = true;
        var rename = () => h.RenameAccountAsync(TenantA, accountId, "After", ct);
        var currency = () => h.ChangeCurrencyAsync(TenantA, accountId, "JPY", 2000, ct);
        await rename.Should().ThrowAsync<InjectedFaultException>();
        await currency.Should().ThrowAsync<InjectedFaultException>();
        armed.Armed = false;

        (await h.AccountsOfAsync(TenantA, ct)).Should().BeEquivalentTo(before, "the account is unchanged");
        (await h.AuditRowsOfAsync(TenantA, ct)).Should().ContainSingle("only the original create is recorded");
    }

    // ---- Story 7, scenario 3: a failed account write leaves no audit record ----

    [Fact]
    public async Task A_failed_account_write_leaves_no_audit_record_for_a_rename_and_a_currency_change()
    {
        var ct = TestContext.Current.CancellationToken;
        var armed = new ThrowOnSaveInterceptor(armed: false);
        await using var h = await CreateAsync(pg, ct, LedgerFaults.Intercept(armed));
        var accountId = (await h.CreateAccountAsync(TenantA, ct, "Before", "EUR", 1000)).AccountId!.Value;
        var before = await h.AccountsOfAsync(TenantA, ct);

        armed.Armed = true;
        var rename = () => h.RenameAccountAsync(TenantA, accountId, "After", ct);
        var currency = () => h.ChangeCurrencyAsync(TenantA, accountId, "JPY", 2000, ct);
        await rename.Should().ThrowAsync<InjectedFaultException>();
        await currency.Should().ThrowAsync<InjectedFaultException>();
        armed.Armed = false;

        (await h.AccountsOfAsync(TenantA, ct)).Should().BeEquivalentTo(before);
        (await h.AuditRowsOfAsync(TenantA, ct)).Should().ContainSingle("no record exists for the failed rename or currency change");
    }

    // ---- Story 7, scenario 4: refusals write no audit record ----

    [Fact]
    public async Task Refusals_write_no_audit_record()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        var first = (await h.CreateAccountAsync(TenantA, ct, "One")).AccountId!.Value;
        (await h.CreateAccountAsync(TenantA, ct, "Two")).Outcome.Should().Be(LedgerOutcome.Succeeded);
        (await h.AuditRowsOfAsync(TenantA, ct)).Should().HaveCount(2);

        // Limit reached.
        (await h.CreateAccountAsync(TenantA, ct, "Three")).Outcome.Should().Be(LedgerOutcome.LimitReached);
        // Not found (a missing id and another space's id).
        (await h.RenameAccountAsync(TenantA, Guid.CreateVersion7(), "Nope", ct)).Outcome.Should().Be(LedgerOutcome.NotFound);
        (await h.ChangeCurrencyAsync(TenantB, first, "JPY", null, ct)).Outcome.Should().Be(LedgerOutcome.NotFound);
        // Currency unchanged.
        (await h.ChangeCurrencyAsync(TenantA, first, "EUR", null, ct)).Outcome.Should().Be(LedgerOutcome.CurrencyUnchanged);
        // Forbidden (no tenant).
        (await h.CreateAccountAsync(TenantResolution.NoTenant, ct)).Outcome.Should().Be(LedgerOutcome.Forbidden);

        (await h.AuditRowsOfAsync(TenantA, ct)).Should().HaveCount(2, "no refusal added a record");
        (await h.AuditRowsOfAsync(TenantB, ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_currency_change_on_an_account_that_holds_a_transaction_is_refused_and_writes_no_audit_record()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct, LedgerFaults.AccountsHoldATransaction());
        var accountId = (await h.CreateAccountAsync(TenantA, ct, "Locked", "EUR", 500)).AccountId!.Value;
        var before = await h.AccountsOfAsync(TenantA, ct);

        (await h.ChangeCurrencyAsync(TenantA, accountId, "JPY", 700, ct)).Outcome.Should().Be(LedgerOutcome.CurrencyLocked);

        (await h.AccountsOfAsync(TenantA, ct)).Should().BeEquivalentTo(before);
        (await h.AuditRowsOfAsync(TenantA, ct)).Should().ContainSingle("only the create");
    }

    // ---- G3 M341-03 (b): the three database checks refuse a malformed row inserted with raw SQL ----

    public static TheoryData<string, string?, bool, string> MalformedLedgerAuditRows => new()
    {
        // action, feature key, subject present, the one check that must refuse it
        { "ledger.account.create", null, false, "ck_audit_records_subject" },
        { "ledger.account.rename", null, false, "ck_audit_records_subject" },
        { "ledger.account.currency_change", null, false, "ck_audit_records_subject" },
        { "entitlements.trial.start", null, true, "ck_audit_records_subject" },
        { "entitlements.override.grant", "forecasting.scenarios", true, "ck_audit_records_subject" },
        { "ledger.account.rename", "forecasting.scenarios", true, "ck_audit_records_feature_key" },
        { "entitlements.override.revoke", null, false, "ck_audit_records_feature_key" },
        { "ledger.account.delete", null, false, "ck_audit_records_action" },
    };

    [Theory]
    [MemberData(nameof(MalformedLedgerAuditRows))]
    public async Task The_database_refuses_a_malformed_audit_row_inserted_with_raw_SQL_as_the_owner_and_as_decisya_ledger(
        string action, string? featureKey, bool withSubject, string expectedCheck)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        foreach (var connectionString in new[] { h.OwnerConnectionString, h.ServiceConnectionString })
        {
            var act = () => InsertRawAuditRowAsync(connectionString, action, featureKey, withSubject ? Guid.CreateVersion7() : null, ct);

            var failure = await act.Should().ThrowAsync<PostgresException>($"{action} / {featureKey} / subject {withSubject}");
            failure.Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            failure.Which.ConstraintName.Should().Be(expectedCheck);
        }

        (await h.AuditRowTextsAsync(ct)).Should().BeEmpty("no malformed row was stored");
    }

    [Fact]
    public async Task The_database_accepts_the_well_formed_audit_rows_of_every_action()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        foreach (var connectionString in new[] { h.OwnerConnectionString, h.ServiceConnectionString })
        {
            (await InsertRawAuditRowAsync(connectionString, "ledger.account.create", null, Guid.CreateVersion7(), ct)).Should().Be(1);
            (await InsertRawAuditRowAsync(connectionString, "ledger.account.rename", null, Guid.CreateVersion7(), ct)).Should().Be(1);
            (await InsertRawAuditRowAsync(connectionString, "ledger.account.currency_change", null, Guid.CreateVersion7(), ct)).Should().Be(1);
            (await InsertRawAuditRowAsync(connectionString, "entitlements.trial.start", null, null, ct)).Should().Be(1);
            (await InsertRawAuditRowAsync(connectionString, "entitlements.override.grant", "forecasting.scenarios", null, ct)).Should().Be(1);
            (await InsertRawAuditRowAsync(connectionString, "entitlements.override.revoke", "forecasting.scenarios", null, ct)).Should().Be(1);
        }

        (await h.AuditRowTextsAsync(ct)).Should().HaveCount(12);
    }

    [Fact]
    public async Task The_audit_migration_adds_the_subject_column_and_the_three_checks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        (await h.QueryAsOwnerAsync(
            "SELECT data_type || '/' || is_nullable FROM information_schema.columns " +
            "WHERE table_schema = 'audit' AND table_name = 'audit_records' AND column_name = 'subject_id'", ct))
            .Should().Equal("uuid/YES");

        foreach (var name in new[] { "ck_audit_records_action", "ck_audit_records_feature_key", "ck_audit_records_subject" })
        {
            (await h.CountAsOwnerAsync(
                $"SELECT count(*) FROM pg_constraint WHERE conname = '{name}' AND conrelid = 'audit.audit_records'::regclass", ct))
                .Should().Be(1, name);
        }

        // The action check holds the six codes, exactly.
        var definition = (await h.QueryAsOwnerAsync(
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ck_audit_records_action' AND conrelid = 'audit.audit_records'::regclass", ct)).Single();
        foreach (var code in new[]
        {
            "entitlements.trial.start", "entitlements.override.grant", "entitlements.override.revoke",
            "ledger.account.create", "ledger.account.rename", "ledger.account.currency_change",
        })
        {
            definition.Should().Contain($"'{code}'");
        }
    }
}
