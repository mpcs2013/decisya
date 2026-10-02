using Decisya.Modules.Audit.Contracts;
using Decisya.Modules.Entitlements.Application;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Entitlements.Domain;
using Decisya.Modules.Entitlements.Tests.TestSupport;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using static Decisya.Modules.Entitlements.Tests.TestSupport.EntitlementsHarness;

namespace Decisya.Modules.Entitlements.Tests;

/// <summary>
/// G3 G4-24-02 and NFR-37 (issue #24): the change and its audit record commit together or not at
/// all. Every test runs as <c>decisya_entitlements</c> against a database migrated by
/// <c>MigrationRunner.RunAsync</c>, so EF's insert is proved to need no SELECT and the savepoint paths
/// are proved under the real role. Every assertion about rows is read as the owner (the application
/// role cannot SELECT the audit table). The fault matrix is command x fault: the append fails after a
/// real append, the real INSERT is refused with 42501, the entitlement write fails, and the commit fails.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EntitlementsAuditAtomicityTests(PostgresFixture pg)
{
    private const int RaceCallCount = 16;
    private static readonly FeatureKey Pro = FeatureKeys.ForecastingScenarios;

    public static TheoryData<string, string> FaultMatrix
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var command in new[] { "StartTrial", "GrantOverride", "RevokeOverride" })
            {
                foreach (var fault in new[] { "append_throws_after_real_append", "insert_privilege_revoked", "save_fails", "commit_fails" })
                {
                    data.Add(command, fault);
                }
            }

            return data;
        }
    }

    private async Task<(EntitlementsHarness Harness, Counter Appends)> HarnessForAsync(string fault, CancellationToken ct)
    {
        var appends = new Counter();
        var configure = fault switch
        {
            "append_throws_after_real_append" => AuditFaults.Decorate(inner => new ThrowAfterAppendWriter(inner)),
            "save_fails" => AuditFaults.All(
                AuditFaults.Decorate(inner => new CountingWriter(inner, appends)), AuditFaults.Intercept(new ThrowOnSaveInterceptor())),
            "commit_fails" => AuditFaults.All(
                AuditFaults.Decorate(inner => new CountingWriter(inner, appends)), AuditFaults.Intercept(new ThrowOnCommitInterceptor())),
            _ => null,
        };

        var h = await CreateAsRoleAsync(pg, ct, configure);
        if (fault == "insert_privilege_revoked")
        {
            // A real database failure: the writer role loses INSERT on the audit table in this database only.
            await h.ExecuteAsOwnerAsync("REVOKE INSERT ON audit.audit_records FROM decisya_entitlements", ct);
        }

        return (h, appends);
    }

    private static async Task SeedOverrideAsync(EntitlementsHarness h, CancellationToken ct)
    {
        await using var db = h.Context(TenantA);
        db.FeatureOverrides.Add(new FeatureOverride(TenantA, Pro, "seeded before the command", Start, null));
        await db.SaveChangesAsync(ct);
    }

    private static async Task<(List<TrialGrant> Trials, List<FeatureOverride> Overrides)> SnapshotAsync(EntitlementsHarness h, CancellationToken ct) =>
        (await h.TrialsOfAsync(TenantA, ct), await h.OverridesOfAsync(TenantA, ct));

    [Theory]
    [MemberData(nameof(FaultMatrix))]
    public async Task If_the_audit_write_fails_or_anything_fails_after_it_or_the_commit_fails_the_commands_change_is_rolled_back(string command, string fault)
    {
        var ct = TestContext.Current.CancellationToken;
        var (h, appends) = await HarnessForAsync(fault, ct);
        await using var _ = h;
        if (command == "RevokeOverride")
        {
            await SeedOverrideAsync(h, ct);
        }

        var before = await SnapshotAsync(h, ct);

        var act = () => EntitlementsAuditTests.Run(h, command, "pilot", ct);

        var failure = await act.Should().ThrowAsync<Exception>($"{command} under {fault} must fail, not swallow the fault");
        if (fault == "insert_privilege_revoked")
        {
            failure.Which.Should().BeOfType<DbUpdateException>();
            failure.Which.InnerException.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }
        else
        {
            failure.Which.Should().BeOfType<InjectedFaultException>();
        }

        var after = await SnapshotAsync(h, ct);
        after.Trials.Should().BeEquivalentTo(before.Trials, "no TrialGrant may appear");
        after.Overrides.Should().BeEquivalentTo(before.Overrides, "no FeatureOverride may appear, and a seeded one must still be there");
        (await h.AuditRowsAsync(ct)).Should().BeEmpty("no audit record may exist without a committed command");

        if (fault == "commit_fails")
        {
            appends.Value.Should().Be(1, "the real append ran before the commit failed, and rolled back with it");
        }

        if (fault == "save_fails")
        {
            appends.Value.Should().Be(0, "the audit append is never reached when the entitlement write fails");
        }
    }

    [Theory]
    [InlineData("StartTrial")]
    [InlineData("GrantOverride")]
    [InlineData("RevokeOverride")]
    public async Task A_healthy_command_commits_its_change_and_its_record_together(string command)
    {
        // The control for the fault matrix: with no fault, the same three commands commit both rows.
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsRoleAsync(pg, ct);
        if (command == "RevokeOverride")
        {
            await SeedOverrideAsync(h, ct);
        }

        (await EntitlementsAuditTests.Run(h, command, "pilot", ct)).IsSuccess.Should().BeTrue();

        (await h.AuditRowsOfAsync(TenantA, ct)).Should().ContainSingle();
        var (trials, overrides) = await SnapshotAsync(h, ct);
        (trials.Count + overrides.Count).Should().Be(command == "RevokeOverride" ? 0 : 1);
    }

    [Fact]
    public async Task A_trial_race_leaves_exactly_one_record_for_the_winner_only()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsRoleAsync(pg, ct);
        var gate = new TaskCompletionSource();

        var calls = Enumerable.Range(0, RaceCallCount)
            .Select(_ => Task.Run(async () =>
            {
                await gate.Task;
                return await h.StartTrialAsync(new StartTrial(TenantA), ct);
            }, ct))
            .ToList();
        gate.SetResult();
        var results = await Task.WhenAll(calls);

        results.Count(r => r.IsSuccess).Should().Be(1, "exactly one start wins");
        results.Where(r => r.IsFailure).Should().HaveCount(RaceCallCount - 1)
            .And.OnlyContain(r => r.Error.Code == "entitlements.trial_already_used");
        (await h.TrialsOfAsync(TenantA, ct)).Should().ContainSingle();
        (await h.AuditRowsOfAsync(TenantA, ct)).Should().ContainSingle().Which.Action.Should().Be("entitlements.trial.start");
    }

    [Fact]
    public async Task Concurrent_grants_for_one_feature_leave_one_override_and_exactly_one_record_per_successful_command()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsRoleAsync(pg, ct);
        var gate = new TaskCompletionSource();

        var calls = Enumerable.Range(0, RaceCallCount)
            .Select(i => Task.Run(async () =>
            {
                await gate.Task;
                return await h.GrantAsync(new GrantOverride(TenantA, Pro, $"grant {i}", null), ct);
            }, ct))
            .ToList();
        gate.SetResult();
        var results = await Task.WhenAll(calls);

        results.Should().OnlyContain(r => r.IsSuccess, "the 23505 loser re-reads and replaces, so every grant succeeds");
        (await h.OverridesOfAsync(TenantA, ct)).Should().ContainSingle();
        var records = await h.AuditRowsOfAsync(TenantA, ct);
        records.Should().HaveCount(results.Count(r => r.IsSuccess), "one record per successful command, no orphan from a failed first attempt");
        records.Should().OnlyContain(r => r.Action == "entitlements.override.grant");
    }

    [Fact]
    public async Task Concurrent_revokes_leave_one_record_per_command()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsRoleAsync(pg, ct);
        await SeedOverrideAsync(h, ct);
        var gate = new TaskCompletionSource();

        var calls = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () =>
            {
                await gate.Task;
                return await h.RevokeAsync(new RevokeOverride(TenantA, Pro), ct);
            }, ct))
            .ToList();
        gate.SetResult();
        var results = await Task.WhenAll(calls);

        results.Should().OnlyContain(r => r.IsSuccess);
        (await h.OverridesOfAsync(TenantA, ct)).Should().BeEmpty();
        (await h.AuditRowsOfAsync(TenantA, ct)).Should().HaveCount(8).And.OnlyContain(r => r.Action == "entitlements.override.revoke");
    }

    /// <summary>
    /// The deterministic version of a concurrent revoke: a competing delete commits just before the
    /// handler's own save, so its DELETE affects no row and EF raises <see cref="DbUpdateConcurrencyException"/>.
    /// The handler rolls back to the savepoint, clears, and still appends its one record.
    /// </summary>
    [Fact]
    public async Task A_revoke_that_loses_the_delete_to_a_concurrent_revoke_still_writes_its_own_one_record()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsRoleAsync(pg, ct);
        await SeedOverrideAsync(h, ct);
        var interceptor = new CompetingDeleteInterceptor(h);
        using var scope = h.Scope(TenantResolution.NoTenant);
        var handler = new RevokeOverrideHandler(
            new TestCurrentTenant { Resolution = TenantResolution.NoTenant },
            h.NewServiceOptions(builder => builder.AddInterceptors(interceptor)),
            new TestCurrentCaller(),
            scope.ServiceProvider.GetRequiredService<IAuditWriter>(),
            NullLogger<RevokeOverrideHandler>.Instance);

        var result = await handler.HandleAsync(new RevokeOverride(TenantA, Pro), ct);

        result.IsSuccess.Should().BeTrue();
        interceptor.CompetingDeletes.Should().Be(1);
        (await h.OverridesOfAsync(TenantA, ct)).Should().BeEmpty();
        (await h.AuditRowsOfAsync(TenantA, ct)).Should().ContainSingle().Which.Action.Should().Be("entitlements.override.revoke");
    }

    [Fact]
    public async Task The_audit_append_runs_on_the_commands_own_connection_and_transaction()
    {
        // A writer that records which transaction it was given: the handler's own, still open, never committed by the writer.
        var ct = TestContext.Current.CancellationToken;
        var seen = new List<(bool HasConnection, bool OpenConnection)>();
        await using var h = await CreateAsRoleAsync(
            pg,
            ct,
            AuditFaults.Decorate(inner => new InspectingWriter(inner, tx => seen.Add((tx.Connection is not null, tx.Connection?.State == System.Data.ConnectionState.Open)))));

        (await h.StartTrialAsync(new StartTrial(TenantA), ct)).IsSuccess.Should().BeTrue();

        seen.Should().ContainSingle().Which.Should().Be((true, true));
    }

    private sealed class InspectingWriter(IAuditWriter inner, Action<System.Data.Common.DbTransaction> inspect) : IAuditWriter
    {
        public async Task AppendAsync(AuditEntry entry, System.Data.Common.DbTransaction transaction, CancellationToken cancellationToken = default)
        {
            inspect(transaction);
            await inner.AppendAsync(entry, transaction, cancellationToken);
        }
    }

    private sealed class CompetingDeleteInterceptor(EntitlementsHarness harness) : SaveChangesInterceptor
    {
        private int _saving;
        private int _deleted;

        public int CompetingDeletes => Volatile.Read(ref _deleted);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _saving) == 1)
            {
                await using var db = harness.Context(TenantA);
                if (await db.FeatureOverrides.ExecuteDeleteAsync(cancellationToken) > 0)
                {
                    Interlocked.Increment(ref _deleted);
                }
            }

            return result;
        }
    }
}
