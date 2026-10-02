using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Decisya.Modules.Audit.Application;
using Decisya.Modules.Audit.Contracts;
using Decisya.Modules.Audit.Domain;
using Decisya.Modules.Audit.Infrastructure;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using Npgsql;

namespace Decisya.Modules.Audit.Tests;

/// <summary>
/// The writer on its own (issue #24, G2 "The writer"; G1 Stories 3 and 4; G3 G4-24-04 and G4-24-05).
/// The unit tests hand it a stub transaction over an unopened connection to an unreachable host: any SQL, or
/// any use of the connection, would fail differently, so a fixed-message refusal proves it happens before
/// any SQL. The integration tests run on a real Postgres 18 database as the owner (the role grants are the
/// migrator tests' and the Entitlements end-to-end tests' job).
/// </summary>
public sealed class AuditWriterTests(PostgresFixture pg)
{
    private static readonly TenantId TenantA = TenantId.From(Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7"));
    private static readonly TenantId TenantB = TenantId.From(Guid.Parse("2f1a8d5e-3b4c-4e6f-9a7b-1c2d3e4f5a6b"));
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 1, 9, 0);
    private const string Actor = "3f2d1c9a-8b7e-4d6c-a5f4-0e1d2c3b4a59";
    private const string Refused = "The audit record was refused";

    private static AuditWriter Writer(TenantResolution ambient, string? userId = Actor) =>
        new(new TestCurrentTenant { Resolution = ambient }, new TestCurrentCaller { Id = userId }, new FakeClock(Now));

    private static AuditEntry Entry(AuditAction action = AuditAction.EntitlementsOverrideGrant, string? featureKey = "forecasting.scenarios", TenantId? tenant = null) =>
        new(tenant ?? TenantA, action, featureKey);

    /// <summary>A transaction over an unopened connection to an unreachable host: it exists only so a refusal can be proved to precede any SQL.</summary>
    private sealed class StubTransaction(bool completed = false) : DbTransaction
    {
        private readonly NpgsqlConnection _connection = new("Host=db.invalid;Database=decisya;Username=placeholder;Password=placeholder;Timeout=1;Command Timeout=1");

        /// <summary>Null for a completed transaction, as <see cref="DbTransaction"/> documents it.</summary>
        protected override DbConnection? DbConnection => completed ? null : _connection;

        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

        public override void Commit() => throw new NotSupportedException("The writer must never commit.");

        public override void Rollback() => throw new NotSupportedException("The writer must never roll back.");

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _connection.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private async Task<string> MigratedDatabaseAsync(CancellationToken ct)
    {
        var owner = new NpgsqlConnectionStringBuilder(await pg.CreateEmptyDatabaseAsync(ct)) { Pooling = false }.ConnectionString;
        var options = new DbContextOptionsBuilder<AuditDbContext>();
        AuditDbContextOptions.Configure(options, owner);
        await using var db = new AuditDbContext(options.Options, new TestCurrentTenant());
        await db.Database.MigrateAsync(ct);
        return owner;
    }

    private static async Task<List<AuditRecord>> RecordsOfAsync(string owner, TenantId tenant, CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<AuditDbContext>();
        AuditDbContextOptions.Configure(options, owner);
        await using var db = new AuditDbContext(options.Options, new TestCurrentTenant { Resolution = TenantResolution.For(tenant) });
        return await db.Records.AsNoTracking().ToListAsync(ct);
    }

    // ---- refusals before any SQL (Unit) ----

    [Trait("Category", "Unit")]
    [Fact]
    public async Task A_null_entry_or_a_null_transaction_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var transaction = new StubTransaction();

        var nullEntry = () => Writer(TenantResolution.NoTenant).AppendAsync(null!, transaction, ct);
        var nullTransaction = () => Writer(TenantResolution.NoTenant).AppendAsync(Entry(), null!, ct);

        await nullEntry.Should().ThrowAsync<ArgumentNullException>();
        await nullTransaction.Should().ThrowAsync<ArgumentNullException>();
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task A_transaction_with_no_connection_is_refused_with_a_fixed_message()
    {
        var ct = TestContext.Current.CancellationToken;
        using var transaction = new StubTransaction(completed: true);

        var act = () => Writer(TenantResolution.NoTenant).AppendAsync(Entry(), transaction, ct);

        var failure = await act.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should().Be("The audit record was refused: the transaction has already completed.");
    }

    [Trait("Category", "Unit")]
    [Theory]
    [InlineData("undefined_action")]
    [InlineData("uninitialized_tenant")]
    [InlineData("trial_with_a_feature_key")]
    [InlineData("grant_without_a_feature_key")]
    [InlineData("revoke_without_a_feature_key")]
    [InlineData("MARKER-9f3a")]
    [InlineData("Upper.case")]
    [InlineData("a.b.c")]
    [InlineData("nodot")]
    [InlineData(".leading")]
    [InlineData("trailing.")]
    [InlineData("1digit.first")]
    [InlineData("white space.key")]
    [InlineData("too_long")]
    public async Task A_malformed_entry_throws_ArgumentException_before_any_SQL_and_the_message_names_no_entry_value(string kind)
    {
        var ct = TestContext.Current.CancellationToken;
        using var transaction = new StubTransaction();
        var entry = kind switch
        {
            "undefined_action" => Entry((AuditAction)99, "forecasting.scenarios"),
            "uninitialized_tenant" => new AuditEntry(default, AuditAction.EntitlementsOverrideGrant, "forecasting.scenarios"),
            "trial_with_a_feature_key" => Entry(AuditAction.EntitlementsTrialStart, "forecasting.scenarios"),
            "grant_without_a_feature_key" => Entry(AuditAction.EntitlementsOverrideGrant, null),
            "revoke_without_a_feature_key" => Entry(AuditAction.EntitlementsOverrideRevoke, null),
            "too_long" => Entry(AuditAction.EntitlementsOverrideGrant, "a." + new string('b', 63)),
            _ => Entry(AuditAction.EntitlementsOverrideGrant, kind),
        };

        var act = () => Writer(TenantResolution.NoTenant).AppendAsync(entry, transaction, ct);

        var failure = await act.Should().ThrowAsync<ArgumentException>();
        failure.Which.Message.Should().NotContain("MARKER-9f3a").And.NotContain("forecasting").And.NotContain(TenantA.Value.ToString());
        failure.Which.InnerException.Should().BeNull();
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task The_writer_refuses_an_Invalid_ambient_and_a_Tenant_ambient_for_another_tenant_before_any_SQL()
    {
        var ct = TestContext.Current.CancellationToken;
        using var transaction = new StubTransaction();

        foreach (var ambient in new[] { TenantResolution.Invalid, TenantResolution.For(TenantB) })
        {
            var act = () => Writer(ambient).AppendAsync(Entry(), transaction, ct);

            var failure = await act.Should().ThrowAsync<InvalidOperationException>();
            failure.Which.Message.Should().StartWith(Refused);
            failure.Which.Message.Should().NotContain(TenantA.Value.ToString()).And.NotContain(TenantB.Value.ToString());
        }
    }

    [Trait("Category", "Unit")]
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tooLong")]
    public async Task The_writer_called_directly_with_no_valid_caller_throws_before_any_SQL_and_never_writes_a_placeholder_actor(string? userId)
    {
        var ct = TestContext.Current.CancellationToken;
        using var transaction = new StubTransaction();
        var actor = userId == "tooLong" ? new string('x', 256) : userId;

        var act = () => Writer(TenantResolution.NoTenant, actor).AppendAsync(Entry(), transaction, ct);

        // A caller with no user id makes the stub (like the real RequestCaller) throw InvalidOperationException; blank and long ids are refused by the writer.
        var failure = await act.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should().NotContain(new string('x', 20));
    }

    // ---- appends (Integration) ----

    [Trait("Category", "Integration")]
    [Theory]
    [InlineData(AuditAction.EntitlementsTrialStart, null, "entitlements.trial.start")]
    [InlineData(AuditAction.EntitlementsOverrideGrant, "forecasting.scenarios", "entitlements.override.grant")]
    [InlineData(AuditAction.EntitlementsOverrideRevoke, "forecasting.scenarios", "entitlements.override.revoke")]
    public async Task Append_writes_one_succeeded_record_with_the_actor_the_clock_time_the_trace_id_and_the_target_tenant(
        AuditAction action, string? featureKey, string storedCode)
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await MigratedDatabaseAsync(ct);
        using var activity = new Activity("test.request");
        activity.SetParentId(ActivityTraceId.CreateFromString("4bf92f3577b34da6a3ce929d0e0e4736"), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
        activity.Start();

        await using (var connection = new NpgsqlConnection(owner))
        {
            await connection.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await Writer(TenantResolution.NoTenant).AppendAsync(new AuditEntry(TenantA, action, featureKey), transaction, ct);
            await transaction.CommitAsync(ct);
        }

        var record = (await RecordsOfAsync(owner, TenantA, ct)).Should().ContainSingle().Which;
        record.Action.Should().Be(action);
        record.Outcome.Should().Be(AuditOutcome.Succeeded);
        record.FeatureKey.Should().Be(featureKey);
        record.ActorUserId.Should().Be(Actor);
        record.OccurredAt.Should().Be(Now);
        record.TraceId.Should().Be("4bf92f3577b34da6a3ce929d0e0e4736");
        record.TenantId.Should().Be(TenantA);
        record.Id.Should().NotBe(Guid.Empty);

        await using var raw = new NpgsqlConnection(owner);
        await raw.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT action FROM audit.audit_records", raw);
        (await command.ExecuteScalarAsync(ct)).Should().Be(storedCode, "the stored code is the explicit map, never the enum name");
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task Append_runs_in_the_callers_transaction_it_never_commits_or_rolls_back_and_leaves_the_connection_open()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await MigratedDatabaseAsync(ct);

        await using (var connection = new NpgsqlConnection(owner))
        {
            await connection.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await Writer(TenantResolution.NoTenant).AppendAsync(Entry(), transaction, ct);

            connection.State.Should().Be(ConnectionState.Open);
            transaction.Connection.Should().BeSameAs(connection, "the transaction is still the caller's, still active");

            // Not committed by the writer: another connection sees nothing, and the caller can still use the transaction.
            (await RecordsOfAsync(owner, TenantA, ct)).Should().BeEmpty();
            await using var inTransaction = new NpgsqlCommand("SELECT count(*) FROM audit.audit_records", connection, transaction);
            (await inTransaction.ExecuteScalarAsync(ct)).Should().Be(1L, "the append is inside the caller's open transaction");

            await transaction.RollbackAsync(ct);
        }

        (await RecordsOfAsync(owner, TenantA, ct)).Should().BeEmpty("a rollback by the caller removes the record");

        await using (var connection = new NpgsqlConnection(owner))
        {
            await connection.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await Writer(TenantResolution.NoTenant).AppendAsync(Entry(), transaction, ct);
            await transaction.CommitAsync(ct);
        }

        (await RecordsOfAsync(owner, TenantA, ct)).Should().ContainSingle("a commit by the caller keeps it");
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task Append_accepts_a_Tenant_ambient_equal_to_the_entrys_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await MigratedDatabaseAsync(ct);

        await using (var connection = new NpgsqlConnection(owner))
        {
            await connection.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await Writer(TenantResolution.For(TenantA)).AppendAsync(Entry(), transaction, ct);
            await transaction.CommitAsync(ct);
        }

        (await RecordsOfAsync(owner, TenantA, ct)).Should().ContainSingle();
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task Append_refuses_a_transaction_that_has_already_completed()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await MigratedDatabaseAsync(ct);

        await using var connection = new NpgsqlConnection(owner);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await transaction.CommitAsync(ct);

        var act = () => Writer(TenantResolution.NoTenant).AppendAsync(Entry(), transaction, ct);

        // Npgsql's own NpgsqlTransaction.Connection throws for a completed transaction (it never returns null),
        // so the refusal is Npgsql's InvalidOperationException; either way nothing is written and no entry value leaks.
        var failure = await act.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should().NotContain("forecasting").And.NotContain(TenantA.Value.ToString());
        (await RecordsOfAsync(owner, TenantA, ct)).Should().BeEmpty();
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task A_database_error_propagates_and_leaves_the_callers_transaction_uncommitted()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await MigratedDatabaseAsync(ct);

        await using (var connection = new NpgsqlConnection(owner))
        {
            await connection.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            // A real failure inside the insert: the owner drops the table the writer is about to use.
            await using (var drop = new NpgsqlCommand("DROP TABLE audit.audit_records", connection, transaction))
            {
                await drop.ExecuteNonQueryAsync(ct);
            }

            var act = () => Writer(TenantResolution.NoTenant).AppendAsync(Entry(), transaction, ct);

            await act.Should().ThrowAsync<DbUpdateException>();
            // The caller's transaction is the caller's to dispose, which rolls it back (the DROP too).
        }

        await using var check = new NpgsqlConnection(owner);
        await check.OpenAsync(ct);
        await using var exists = new NpgsqlCommand("SELECT to_regclass('audit.audit_records') IS NOT NULL", check);
        (await exists.ExecuteScalarAsync(ct)).Should().Be(true, "disposing the uncommitted transaction rolled everything back");
    }
}

/// <summary>
/// G1 Story 3 last scenario and G2 "Telemetry": a missing activity never fails the write and leaves a null
/// trace id, and the writer's one activity carries only the action code. ActivityListeners are
/// process-wide, so these run alone: another test's listener would make a handler create an activity.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SerialTelemetryGroup.Name)]
public sealed class AuditWriterTelemetryTests(PostgresFixture pg)
{
    private static readonly TenantId TenantA = TenantId.From(Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7"));

    private async Task<string> MigratedDatabaseAsync(CancellationToken ct)
    {
        var owner = new NpgsqlConnectionStringBuilder(await pg.CreateEmptyDatabaseAsync(ct)) { Pooling = false }.ConnectionString;
        var options = new DbContextOptionsBuilder<AuditDbContext>();
        AuditDbContextOptions.Configure(options, owner);
        await using var db = new AuditDbContext(options.Options, new TestCurrentTenant());
        await db.Database.MigrateAsync(ct);
        return owner;
    }

    private static AuditWriter Writer() =>
        new(new TestCurrentTenant { Resolution = TenantResolution.NoTenant }, new TestCurrentCaller(), new FakeClock(Instant.FromUtc(2026, 10, 1, 9, 0)));

    [Fact]
    public async Task A_missing_activity_does_not_fail_the_write_and_the_trace_id_is_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await MigratedDatabaseAsync(ct);
        Activity.Current = null;

        await using var connection = new NpgsqlConnection(owner);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await Writer().AppendAsync(new AuditEntry(TenantA, AuditAction.EntitlementsTrialStart, null), transaction, ct);
        await transaction.CommitAsync(ct);

        await using var read = new NpgsqlCommand("SELECT trace_id IS NULL FROM audit.audit_records", connection);
        (await read.ExecuteScalarAsync(ct)).Should().Be(true);
    }

    [Fact]
    public async Task The_writers_one_activity_is_Audit_Append_tagged_only_with_the_action_code()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await MigratedDatabaseAsync(ct);
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AuditModule.TelemetryName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);

        await using var connection = new NpgsqlConnection(owner);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await Writer().AppendAsync(new AuditEntry(TenantA, AuditAction.EntitlementsOverrideGrant, "forecasting.scenarios"), transaction, ct);
        await transaction.CommitAsync(ct);

        var activity = stopped.Should().ContainSingle().Which;
        activity.DisplayName.Should().Be("Audit.Append");
        activity.TagObjects.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, object?>("decisya.audit.action", "entitlements.override.grant"));
        activity.Events.Should().BeEmpty();
    }
}

/// <summary>See <c>SerialTelemetryGroup</c> in the Entitlements tests: telemetry listeners are process-wide.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialTelemetryGroup
{
    public const string Name = "No other test may listen to telemetry while this one runs";
}
