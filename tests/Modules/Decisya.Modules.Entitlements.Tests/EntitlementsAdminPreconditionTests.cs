using Decisya.Modules.Entitlements.Application;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Entitlements.Contracts.Admin;
using Decisya.Modules.Entitlements.Domain;
using Decisya.Modules.Entitlements.Tests.TestSupport;
using Decisya.SharedKernel.Results;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using NodaTime;
using static Decisya.Modules.Entitlements.Tests.TestSupport.EntitlementsHarness;

namespace Decisya.Modules.Entitlements.Tests;

/// <summary>
/// Issue #25 (G3 G4-25-01, C-4; ADR-0012 amendment 1 points 2 and 6): the handler precondition
/// <c>Kind == None &amp;&amp; IsPlatformAdmin</c>, the target-tenant existence check, and the two new
/// fault-matrix rows per handler. The unit tests run on a placeholder host, so any connection
/// attempt (and with it any transaction) would throw.
/// </summary>
public sealed class EntitlementsAdminPreconditionTests(PostgresFixture pg)
{
    private static readonly FeatureKey Pro = FeatureKeys.ForecastingScenarios;

    public static TheoryData<string> CommandNames => new() { "StartTrial", "GrantOverride", "RevokeOverride" };

    public static TheoryData<string, string> CommandsByFault
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var command in new[] { "StartTrial", "GrantOverride", "RevokeOverride" })
            {
                foreach (var fault in new[] { "existence_check_throws", "existence_check_returns_false" })
                {
                    data.Add(command, fault);
                }
            }

            return data;
        }
    }

    private static Task<Result> Run(EntitlementsHarness h, string command, TenantResolution ambient, string reason = "pilot", CancellationToken ct = default) =>
        command switch
        {
            "StartTrial" => h.StartTrialAsync(new StartTrial(TenantA), ct, ambient),
            "GrantOverride" => h.GrantAsync(new GrantOverride(TenantA, Pro, reason, null), ct, ambient),
            "RevokeOverride" => h.RevokeAsync(new RevokeOverride(TenantA, Pro), ct, ambient),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
        };

    // ---- the precondition ----

    [Trait("Category", "Unit")]
    [Theory]
    [MemberData(nameof(CommandNames))]
    public async Task A_tenant_less_caller_without_the_platform_admin_flag_gets_forbidden_with_zero_database_commands(string command)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = CreateUnreachable();
        h.CallerIsPlatformAdmin = false;

        var result = await Run(h, command, TenantResolution.NoTenant, ct: ct);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(EntitlementAdminErrorCodes.Forbidden);
        result.Error.Category.Should().Be(ErrorCategory.Forbidden);
        h.TenantExistence.Calls.Should().BeEmpty("the precondition runs before the existence check");
    }

    [Trait("Category", "Unit")]
    [Theory]
    [MemberData(nameof(CommandNames))]
    public async Task A_Tenant_caller_gets_forbidden_even_with_the_platform_admin_flag_and_zero_database_commands(string command)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = CreateUnreachable();
        h.CallerIsPlatformAdmin = true;

        var result = await Run(h, command, TenantResolution.For(TenantB), ct: ct);

        result.Error.Code.Should().Be(EntitlementAdminErrorCodes.Forbidden);
        h.TenantExistence.Calls.Should().BeEmpty();
    }

    [Trait("Category", "Unit")]
    [Theory]
    [MemberData(nameof(CommandNames))]
    public async Task An_Invalid_ambient_gets_forbidden_whatever_the_platform_admin_flag(string command)
    {
        var ct = TestContext.Current.CancellationToken;
        foreach (var isAdmin in new[] { true, false })
        {
            await using var h = CreateUnreachable();
            h.CallerIsPlatformAdmin = isAdmin;

            var result = await Run(h, command, TenantResolution.Invalid, ct: ct);

            result.Error.Code.Should().Be(EntitlementAdminErrorCodes.Forbidden, $"isAdmin={isAdmin}");
            h.TenantExistence.Calls.Should().BeEmpty();
        }
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task A_refused_non_admin_call_learns_no_validation_code()
    {
        // The command is invalid in every way (default tenant, default feature, empty reason): only a
        // precondition that runs first gives "forbidden", never a catalog or validation code.
        var ct = TestContext.Current.CancellationToken;
        await using var h = CreateUnreachable();
        h.CallerIsPlatformAdmin = false;

        var results = new[]
        {
            await h.StartTrialAsync(new StartTrial(default), ct),
            await h.GrantAsync(new GrantOverride(default, default, string.Empty, Instant.MinValue), ct),
            await h.RevokeAsync(new RevokeOverride(default, default), ct),
        };

        results.Should().OnlyContain(r => r.Error.Code == EntitlementAdminErrorCodes.Forbidden);
    }

    // ---- the existence check (S-5) ----

    [Trait("Category", "Unit")]
    [Theory]
    [MemberData(nameof(CommandNames))]
    public async Task A_missing_target_tenant_gives_tenant_not_found_before_any_transaction(string command)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = CreateUnreachable();
        h.TenantExistence.Existing.Remove(TenantA);

        // On the placeholder host any connection (so any BEGIN) would throw instead of returning a Result.
        var result = await Run(h, command, TenantResolution.NoTenant, ct: ct);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("entitlements.tenant_not_found");
        result.Error.Category.Should().Be(ErrorCategory.NotFound);
        h.TenantExistence.Calls.Should().ContainSingle().Which.Should().Be(TenantResolution.For(TenantA), "the handler passes the scope it minted for the target tenant");
        h.Logs.Records.Should().ContainSingle(r => r.Level == Microsoft.Extensions.Logging.LogLevel.Warning && r.Contains(TenantA.ToString()));
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task Validation_comes_before_the_existence_check()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = CreateUnreachable();
        h.TenantExistence.Existing.Clear();

        var badReason = await h.GrantAsync(new GrantOverride(TenantA, Pro, string.Empty, null), ct);
        var badFeature = await h.GrantAsync(new GrantOverride(TenantA, FeatureKey.Create("nosuch.feature"), "pilot", null), ct);

        badReason.Error.Code.Should().Be(EntitlementAdminErrorCodes.ReasonInvalid);
        badFeature.Error.Code.Should().Be(EntitlementAdminErrorCodes.FeatureUnknown);
        h.TenantExistence.Calls.Should().BeEmpty();
    }

    [Trait("Category", "Unit")]
    [Theory]
    [MemberData(nameof(CommandNames))]
    public async Task A_failing_existence_check_propagates_and_no_transaction_is_opened(string command)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = CreateUnreachable();
        h.TenantExistence.Fault = new InjectedFaultException("Injected fault in the existence check.");

        var act = () => Run(h, command, TenantResolution.NoTenant, ct: ct);

        // InjectedFaultException, not the placeholder host's NpgsqlException: no connection was attempted.
        await act.Should().ThrowAsync<InjectedFaultException>();
    }

    // ---- the fault matrix: two new rows per handler (C-4) ----

    [Trait("Category", "Integration")]
    [Theory]
    [MemberData(nameof(CommandsByFault))]
    public async Task If_the_existence_check_throws_or_returns_false_nothing_is_written_and_no_database_command_runs(string command, string fault)
    {
        var ct = TestContext.Current.CancellationToken;
        var commands = new CommandCountingInterceptor();
        await using var h = await CreateAsRoleAsync(pg, ct, AuditFaults.Intercept(commands));
        if (command == "RevokeOverride")
        {
            await using var db = h.Context(TenantA);
            db.FeatureOverrides.Add(new FeatureOverride(TenantA, Pro, "seeded before the command", Start, null));
            await db.SaveChangesAsync(ct);
        }

        var trialsBefore = await h.TrialsOfAsync(TenantA, ct);
        var overridesBefore = await h.OverridesOfAsync(TenantA, ct);
        var seededCommands = commands.Count;

        if (fault == "existence_check_throws")
        {
            h.TenantExistence.Fault = new InjectedFaultException("Injected fault in the existence check.");
            var act = () => Run(h, command, TenantResolution.NoTenant, ct: ct);
            await act.Should().ThrowAsync<InjectedFaultException>();
        }
        else
        {
            h.TenantExistence.Existing.Remove(TenantA);
            var result = await Run(h, command, TenantResolution.NoTenant, ct: ct);
            result.Error.Code.Should().Be("entitlements.tenant_not_found");
        }

        commands.Count.Should().Be(seededCommands, "the check runs before the handler's context sends anything (no BEGIN)");
        (await h.TrialsOfAsync(TenantA, ct)).Should().BeEquivalentTo(trialsBefore);
        (await h.OverridesOfAsync(TenantA, ct)).Should().BeEquivalentTo(overridesBefore);
        (await h.AuditRowsAsync(ct)).Should().BeEmpty("no audit record without a committed command");
    }

    [Trait("Category", "Integration")]
    [Theory]
    [MemberData(nameof(CommandNames))]
    public async Task A_non_admin_tenant_less_caller_writes_nothing_on_a_real_database(string command)
    {
        var ct = TestContext.Current.CancellationToken;
        var commands = new CommandCountingInterceptor();
        await using var h = await CreateAsRoleAsync(pg, ct, AuditFaults.Intercept(commands));
        h.CallerIsPlatformAdmin = false;

        var result = await Run(h, command, TenantResolution.NoTenant, ct: ct);

        result.Error.Code.Should().Be(EntitlementAdminErrorCodes.Forbidden);
        commands.Count.Should().Be(0);
        (await h.TotalRowsAsync(ct)).Should().Be(0);
        (await h.AuditRowsAsync(ct)).Should().BeEmpty();
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task An_existing_target_tenant_still_succeeds_and_writes_exactly_one_audit_record()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsRoleAsync(pg, ct);

        var result = await Run(h, "GrantOverride", TenantResolution.NoTenant, ct: ct);

        result.IsSuccess.Should().BeTrue();
        h.TenantExistence.Calls.Should().ContainSingle().Which.Should().Be(TenantResolution.For(TenantA));
        (await h.AuditRowsOfAsync(TenantA, ct)).Should().ContainSingle();
    }

    // ---- the reason canary ----

    [Trait("Category", "Unit")]
    [Fact]
    public void GrantOverride_ToString_and_PrintMembers_never_render_the_reason()
    {
        var marker = $"MARKER-{Guid.NewGuid():N}";
        var command = new GrantOverride(TenantA, Pro, marker, Start + Duration.FromDays(1));

        var text = command.ToString();
        $"{command}".Should().NotContain(marker);
        text.Should().NotContain(marker).And.NotContain("Reason");
        text.Should().Contain(TenantA.ToString()).And.Contain(Pro.ToString());
    }
}
