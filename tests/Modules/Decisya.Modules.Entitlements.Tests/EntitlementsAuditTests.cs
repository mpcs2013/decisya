using System.Diagnostics;
using Decisya.Modules.Entitlements.Application;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Entitlements.Tests.TestSupport;
using Decisya.SharedKernel.Results;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using NodaTime;
using static Decisya.Modules.Entitlements.Tests.TestSupport.EntitlementsHarness;

namespace Decisya.Modules.Entitlements.Tests;

/// <summary>
/// G1 Stories 2 to 5, G3 G4-24-02 (record content), G4-24-04 and G4-24-05, end to end: the three
/// cross-tenant admin commands run through the real DI wiring, against a database migrated by
/// <c>MigrationRunner.RunAsync</c>, connected as <c>decisya_entitlements</c> (INSERT-only on the audit
/// table). Every assertion about audit rows is read as the database owner. Test names are the
/// Gherkin scenario titles.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EntitlementsAuditTests(PostgresFixture pg)
{
    private const string TraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private const string Marker = "MARKER-9f3a Contact: anna.meier@example.com, IBAN DE89370400440532013000";
    private static readonly FeatureKey Pro = FeatureKeys.ForecastingScenarios;

    /// <summary>An ambient activity whose trace id is fixed. Disposing it restores the previous current activity.</summary>
    private static Activity StartActivity()
    {
        var activity = new Activity("test.request");
        activity.SetParentId(ActivityTraceId.CreateFromString(TraceId), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
        return activity.Start();
    }

    // ---- Story 2 ----

    [Fact]
    public async Task Starting_a_trial_writes_one_audit_record()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsRoleAsync(pg, ct);
        using var activity = StartActivity();

        (await h.StartTrialAsync(new StartTrial(TenantA), ct)).IsSuccess.Should().BeTrue();

        (await h.TrialsOfAsync(TenantA, ct)).Should().ContainSingle();
        var record = (await h.AuditRowsOfAsync(TenantA, ct)).Should().ContainSingle().Which;
        record.Action.Should().Be("entitlements.trial.start");
        record.Outcome.Should().Be("succeeded");
        record.ActorUserId.Should().Be("3f2d1c9a-8b7e-4d6c-a5f4-0e1d2c3b4a59");
        record.OccurredAt.Should().Be(Instant.FromUtc(2026, 10, 1, 9, 0));
        record.TraceId.Should().Be(TraceId);
        record.FeatureKey.Should().BeNull("a trial carries no feature key");
        record.TenantId.Should().Be(TenantA.Value, "the record belongs to the command's target tenant");
    }

    [Fact]
    public async Task Granting_an_override_writes_one_audit_record_naming_the_feature()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsRoleAsync(pg, ct);

        (await h.GrantAsync(new GrantOverride(TenantA, Pro, "Design-partner pilot", null), ct)).IsSuccess.Should().BeTrue();

        (await h.OverridesOfAsync(TenantA, ct)).Should().ContainSingle();
        var record = (await h.AuditRowsOfAsync(TenantA, ct)).Should().ContainSingle().Which;
        record.Action.Should().Be("entitlements.override.grant");
        record.FeatureKey.Should().Be("forecasting.scenarios");
        record.Outcome.Should().Be("succeeded");
    }

    [Fact]
    public async Task Replacing_an_existing_override_writes_a_further_record_one_per_command()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsRoleAsync(pg, ct);
        (await h.GrantAsync(new GrantOverride(TenantA, Pro, "first", null), ct)).IsSuccess.Should().BeTrue();

        (await h.GrantAsync(new GrantOverride(TenantA, Pro, "second", null), ct)).IsSuccess.Should().BeTrue();

        (await h.OverridesOfAsync(TenantA, ct)).Should().ContainSingle();
        var records = await h.AuditRowsOfAsync(TenantA, ct);
        records.Should().HaveCount(2);
        records.Should().OnlyContain(r => r.Action == "entitlements.override.grant");
    }

    [Fact]
    public async Task Revoking_an_override_writes_one_audit_record()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsRoleAsync(pg, ct);
        (await h.GrantAsync(new GrantOverride(TenantA, Pro, "pilot", null), ct)).IsSuccess.Should().BeTrue();

        (await h.RevokeAsync(new RevokeOverride(TenantA, Pro), ct)).IsSuccess.Should().BeTrue();

        (await h.OverridesOfAsync(TenantA, ct)).Should().BeEmpty();
        var revoke = (await h.AuditRowsOfAsync(TenantA, ct)).Should().ContainSingle(r => r.Action == "entitlements.override.revoke").Which;
        revoke.FeatureKey.Should().Be("forecasting.scenarios");
    }

    [Fact]
    public async Task Revoking_a_feature_that_has_no_override_still_records_the_admin_action()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsRoleAsync(pg, ct);

        (await h.RevokeAsync(new RevokeOverride(TenantA, Pro), ct)).IsSuccess.Should().BeTrue("the outcome asked for already holds");

        (await h.OverridesOfAsync(TenantA, ct)).Should().BeEmpty();
        (await h.AuditRowsOfAsync(TenantA, ct)).Should().ContainSingle().Which.Action.Should().Be("entitlements.override.revoke");
    }

    [Fact]
    public async Task Refused_attempts_that_change_nothing_write_no_audit_record()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsRoleAsync(pg, ct);

        (await h.StartTrialAsync(new StartTrial(TenantA), ct)).IsSuccess.Should().BeTrue();
        var second = await h.StartTrialAsync(new StartTrial(TenantA), ct);
        second.Error.Code.Should().Be("entitlements.trial_already_used");
        (await h.AuditRowsOfAsync(TenantA, ct)).Should().ContainSingle(r => r.Action == "entitlements.trial.start");

        var unknownFeature = await h.GrantAsync(new GrantOverride(TenantA, FeatureKey.Create("nosuch.feature"), "pilot", null), ct);
        var emptyReason = await h.GrantAsync(new GrantOverride(TenantA, Pro, string.Empty, null), ct);
        var pastExpiry = await h.GrantAsync(new GrantOverride(TenantA, Pro, "pilot", Start - Duration.FromDays(1)), ct);
        var defaultRevokeKey = await h.RevokeAsync(new RevokeOverride(TenantA, default), ct);
        var noTenant = await h.GrantAsync(new GrantOverride(default, Pro, "pilot", null), ct);

        unknownFeature.Error.Code.Should().Be("entitlements.feature_unknown");
        emptyReason.Error.Code.Should().Be("entitlements.reason_invalid");
        pastExpiry.Error.Code.Should().Be("entitlements.expiry_not_in_future");
        defaultRevokeKey.Error.Code.Should().Be("entitlements.feature_unknown");
        noTenant.Error.Code.Should().Be("entitlements.tenant_invalid");

        (await h.AuditRowsAsync(ct)).Should().ContainSingle("only the one successful trial start was recorded");
        (await h.OverridesOfAsync(TenantA, ct)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Tenant")]
    [InlineData("Invalid")]
    public async Task A_non_admin_ambient_is_refused_with_no_database_access_and_no_audit_record(string ambientName)
    {
        var ct = TestContext.Current.CancellationToken;
        var commands = new CommandCountingInterceptor();
        var appends = new Counter();
        await using var h = await CreateAsRoleAsync(
            pg, ct, AuditFaults.All(AuditFaults.Intercept(commands), AuditFaults.Decorate(inner => new CountingWriter(inner, appends))));
        var ambient = ambientName == "Tenant" ? TenantResolution.For(TenantA) : TenantResolution.Invalid;

        var trial = await h.StartTrialAsync(new StartTrial(default), ct, ambient);
        var grant = await h.GrantAsync(new GrantOverride(default, default, string.Empty, Instant.MinValue), ct, ambient);
        var revoke = await h.RevokeAsync(new RevokeOverride(default, default), ct, ambient);

        foreach (var result in new[] { trial, grant, revoke })
        {
            result.Error.Code.Should().Be("entitlements.forbidden");
        }

        commands.Count.Should().Be(0, "a refusal must not touch the database");
        appends.Value.Should().Be(0);
        (await h.TotalRowsAsync(ct)).Should().Be(0);
        (await h.AuditRowsAsync(ct)).Should().BeEmpty();
    }

    // ---- Story 3 ----

    [Fact]
    public async Task The_override_reason_never_reaches_the_audit_record()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsRoleAsync(pg, ct);

        (await h.GrantAsync(new GrantOverride(TenantA, Pro, Marker, null), ct)).IsSuccess.Should().BeTrue();
        (await h.GrantAsync(new GrantOverride(TenantA, Pro, Marker + " replaced", null), ct)).IsSuccess.Should().BeTrue();
        (await h.RevokeAsync(new RevokeOverride(TenantA, Pro), ct)).IsSuccess.Should().BeTrue();

        // The override row itself holds the reason (#23); the audit table must not, in any column.
        (await h.AuditRowsAsync(ct)).Should().HaveCount(3);
        var wholeTable = await h.QueryAsOwnerAsync("SELECT to_jsonb(t)::text FROM audit.audit_records t", ct);
        wholeTable.Should().HaveCount(3);
        wholeTable.Should().NotContain(row => row.Contains("MARKER-9f3a", StringComparison.Ordinal));
        wholeTable.Should().NotContain(row => row.Contains("anna.meier@example.com", StringComparison.Ordinal));
        wholeTable.Should().NotContain(row => row.Contains("DE89370400440532013000", StringComparison.Ordinal));

        // And the table has no column for a reason or a command payload.
        (await h.QueryAsOwnerAsync(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = 'audit' AND table_name = 'audit_records' ORDER BY column_name", ct))
            .Should().Equal("action", "actor_user_id", "feature_key", "id", "occurred_at", "outcome", "tenant_id", "trace_id");
    }

    [Theory]
    [InlineData("success")]
    [InlineData("append_throws_after_real_append")]
    [InlineData("insert_privilege_revoked")]
    [InlineData("commit_fails")]
    [InlineData("save_fails")]
    public async Task The_reason_does_not_appear_in_logs_error_text_or_telemetry_of_the_audit_write(string mode)
    {
        var ct = TestContext.Current.CancellationToken;
        using var telemetry = new TelemetryCapture();
        var configure = mode switch
        {
            "append_throws_after_real_append" => AuditFaults.Decorate(inner => new ThrowAfterAppendWriter(inner)),
            "commit_fails" => AuditFaults.Intercept(new ThrowOnCommitInterceptor()),
            "save_fails" => AuditFaults.Intercept(new ThrowOnSaveInterceptor()),
            _ => null,
        };
        await using var h = await CreateAsRoleAsync(pg, ct, configure);
        if (mode == "insert_privilege_revoked")
        {
            await h.ExecuteAsOwnerAsync("REVOKE INSERT ON audit.audit_records FROM decisya_entitlements", ct);
        }

        var texts = new List<string>();
        try
        {
            var result = await h.GrantAsync(new GrantOverride(TenantA, Pro, Marker, null), ct);
            texts.Add(result.ToString() ?? string.Empty);
            if (result.IsFailure)
            {
                texts.Add(result.Error.Message);
            }

            (mode == "success").Should().BeTrue("only the success mode returns a result");
        }
        catch (Exception ex)
        {
            mode.Should().NotBe("success");
            texts.Add(ex.ToString());
            for (Exception? inner = ex; inner is not null; inner = inner.InnerException)
            {
                texts.Add(inner.Message);
            }
        }

        texts.Should().NotBeEmpty();
        texts.Should().NotContain(t => t.Contains("MARKER-9f3a", StringComparison.Ordinal), "no exception text or error message may carry the reason");
        h.Logs.Records.Should().NotBeEmpty();
        h.Logs.Records.Where(r => r.Contains("MARKER-9f3a")).Should().BeEmpty("no log record may carry the reason");
        h.Logs.Records.Where(r => r.Contains("anna.meier@example.com") || r.Contains("DE89370400440532013000")).Should().BeEmpty();
        telemetry.Values.Should().NotBeEmpty();
        telemetry.Values.Should().NotContain(v => v.Contains("MARKER-9f3a", StringComparison.Ordinal), "no metric or activity tag may carry the reason");

        if (mode == "success")
        {
            // The audit write's own activity exists and carries only the action code.
            telemetry.Values.Should().Contain("activity=Decisya.Audit/Audit.Append");
            telemetry.Values.Should().Contain("decisya.audit.action=entitlements.override.grant");
        }

        // None of the failure modes leaves a record: the marker is also absent from the table.
        var wholeTable = await h.QueryAsOwnerAsync("SELECT to_jsonb(t)::text FROM audit.audit_records t", ct);
        wholeTable.Should().HaveCount(mode == "success" ? 1 : 0);
        wholeTable.Should().NotContain(row => row.Contains("MARKER-9f3a", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_trace_id_is_the_correlation_id()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsRoleAsync(pg, ct);
        using var activity = StartActivity();

        (await h.GrantAsync(new GrantOverride(TenantA, Pro, "pilot", null), ct)).IsSuccess.Should().BeTrue();
        (await h.RevokeAsync(new RevokeOverride(TenantA, Pro), ct)).IsSuccess.Should().BeTrue();

        (await h.AuditRowsOfAsync(TenantA, ct)).Should().OnlyContain(r => r.TraceId == TraceId).And.HaveCount(2);
    }

    // ---- Story 4 ----

    [Theory]
    [InlineData("StartTrial")]
    [InlineData("GrantOverride")]
    [InlineData("RevokeOverride")]
    public async Task The_actor_is_the_ambient_callers_user_id(string command)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsRoleAsync(pg, ct);
        h.CallerUserId = "a7e5c0de-1f2b-4c3d-8e9f-0a1b2c3d4e5f";

        (await Run(h, command, "pilot", ct)).IsSuccess.Should().BeTrue();

        (await h.AuditRowsOfAsync(TenantA, ct)).Should().ContainSingle().Which.ActorUserId.Should().Be("a7e5c0de-1f2b-4c3d-8e9f-0a1b2c3d4e5f");
    }

    // ---- Story 5 ----

    [Fact]
    public async Task A_command_for_tenant_A_adds_records_only_for_tenant_A()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsRoleAsync(pg, ct);
        (await h.StartTrialAsync(new StartTrial(TenantB), ct)).IsSuccess.Should().BeTrue();
        (await h.GrantAsync(new GrantOverride(TenantB, Pro, "b", null), ct)).IsSuccess.Should().BeTrue();
        var before = await h.AuditRowsOfAsync(TenantB, ct);
        before.Should().HaveCount(2);

        h.Clock.Advance(Duration.FromHours(1));
        (await h.StartTrialAsync(new StartTrial(TenantA), ct)).IsSuccess.Should().BeTrue();
        (await h.GrantAsync(new GrantOverride(TenantA, Pro, "a", null), ct)).IsSuccess.Should().BeTrue();
        (await h.RevokeAsync(new RevokeOverride(TenantA, Pro), ct)).IsSuccess.Should().BeTrue();

        (await h.AuditRowsOfAsync(TenantB, ct)).Should().BeEquivalentTo(before, "tenant B's records are unchanged");
        (await h.AuditRowsOfAsync(TenantA, ct)).Should().HaveCount(3);
        (await h.AuditRowsOfAsync(TenantC, ct)).Should().BeEmpty();
        (await h.AuditRowsAsync(ct)).Should().OnlyContain(r => r.TenantId == TenantA.Value || r.TenantId == TenantB.Value);
    }

    internal static Task<Result> Run(EntitlementsHarness h, string command, string reason, CancellationToken ct) => command switch
    {
        "StartTrial" => h.StartTrialAsync(new StartTrial(TenantA), ct),
        "GrantOverride" => h.GrantAsync(new GrantOverride(TenantA, Pro, reason, null), ct),
        "RevokeOverride" => h.RevokeAsync(new RevokeOverride(TenantA, Pro), ct),
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
    };
}

/// <summary>
/// G1 Story 3, last scenario: "no activity is current". Runs alone (see <see cref="SerialTelemetryGroup"/>)
/// because any other test's activity listener would make the handler create an activity, and with it a trace id.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SerialTelemetryGroup.Name)]
public sealed class EntitlementsAuditNoActivityTests(PostgresFixture pg)
{
    [Fact]
    public async Task The_trace_id_is_null_and_a_missing_activity_does_not_fail_the_command()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsRoleAsync(pg, ct);
        Activity.Current = null;

        (await h.StartTrialAsync(new StartTrial(TenantA), ct)).IsSuccess.Should().BeTrue();

        Activity.Current.Should().BeNull();
        (await h.AuditRowsOfAsync(TenantA, ct)).Should().ContainSingle().Which.TraceId.Should().BeNull();
    }
}
