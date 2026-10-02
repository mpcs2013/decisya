using Decisya.Modules.Entitlements.Application;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Entitlements.Tests.TestSupport;
using Decisya.SharedKernel.Results;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.Extensions.Logging;
using NodaTime;
using static Decisya.Modules.Entitlements.Tests.TestSupport.EntitlementsHarness;

namespace Decisya.Modules.Entitlements.Tests;

/// <summary>
/// G1 Story 4 and G3 G4-24-04 (T-02, T-06): the actor is the ambient caller's validated user id, never a
/// placeholder. The check order is Forbidden, then actor, then validation, with no database access
/// before any of them. "No database access" is proved twice: on an unreachable host (any connection
/// attempt throws) and with a <c>DbCommandInterceptor</c> on a reachable database (zero commands).
/// </summary>
public sealed class EntitlementsAuditActorTests(PostgresFixture pg)
{
    private static Task<Result>[] AllCommandsWithInvalidInput(EntitlementsHarness h, TenantResolution ambient, CancellationToken ct) =>
    [
        h.StartTrialAsync(new StartTrial(default), ct, ambient),
        h.GrantAsync(new GrantOverride(default, default, string.Empty, Instant.MinValue), ct, ambient),
        h.RevokeAsync(new RevokeOverride(default, default), ct, ambient),
    ];

    [Trait("Category", "Unit")]
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_command_with_no_identified_caller_is_refused_with_actor_unknown_and_no_database_access(string? userId)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = CreateUnreachable();
        h.CallerUserId = userId;

        // Valid input: only the actor check can produce this result, and any database access would throw.
        var trial = await h.StartTrialAsync(new StartTrial(TenantA), ct);
        var grant = await h.GrantAsync(new GrantOverride(TenantA, FeatureKeys.ForecastingScenarios, "pilot", null), ct);
        var revoke = await h.RevokeAsync(new RevokeOverride(TenantA, FeatureKeys.ForecastingScenarios), ct);

        foreach (var result in new[] { trial, grant, revoke })
        {
            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("entitlements.actor_unknown");
            result.Error.Category.Should().Be(ErrorCategory.Forbidden);
            result.Error.Message.Should().Be("An entitlement admin command was refused: the caller has no validated user id.");
        }

        // The log line names the command and nothing else.
        var warnings = h.Logs.Records.Where(r => r.Level == LogLevel.Warning).ToList();
        warnings.Should().HaveCount(3);
        warnings.Should().OnlyContain(r => r.Message.Contains("has no validated user id", StringComparison.Ordinal));
    }

    [Trait("Category", "Unit")]
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task The_actor_check_runs_before_validation_and_after_the_Forbidden_precondition(string? userId)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = CreateUnreachable();
        h.CallerUserId = userId;

        // Under a "tenant" or "invalid" ambient with no user id: forbidden, not actor_unknown.
        foreach (var ambient in new[] { TenantResolution.For(TenantA), TenantResolution.Invalid })
        {
            foreach (var result in await Task.WhenAll(AllCommandsWithInvalidInput(h, ambient, ct)))
            {
                result.Error.Code.Should().Be("entitlements.forbidden");
            }
        }

        // Under "no tenant" with no user id: actor_unknown, not a validation error.
        foreach (var result in await Task.WhenAll(AllCommandsWithInvalidInput(h, TenantResolution.NoTenant, ct)))
        {
            result.Error.Code.Should().Be("entitlements.actor_unknown");
        }

        // With a user id, the same input reaches validation.
        h.CallerUserId = TestCurrentCaller.DefaultUserId;
        var validation = await Task.WhenAll(AllCommandsWithInvalidInput(h, TenantResolution.NoTenant, ct));
        validation.Should().OnlyContain(r => r.Error.Code == "entitlements.tenant_invalid");
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task No_refusal_issues_a_database_command_and_no_audit_append_but_a_valid_command_does()
    {
        var ct = TestContext.Current.CancellationToken;
        var commands = new CommandCountingInterceptor();
        var appends = new Counter();
        await using var h = await CreateAsRoleAsync(
            pg, ct, AuditFaults.All(AuditFaults.Intercept(commands), AuditFaults.Decorate(inner => new CountingWriter(inner, appends))));

        // Forbidden (Tenant, Invalid), then actor_unknown (null, blank), then every validation failure, all with a reachable database.
        foreach (var ambient in new[] { TenantResolution.For(TenantA), TenantResolution.Invalid })
        {
            (await Task.WhenAll(AllCommandsWithInvalidInput(h, ambient, ct))).Should().OnlyContain(r => r.Error.Code == "entitlements.forbidden");
        }

        foreach (var userId in new string?[] { null, string.Empty, "   " })
        {
            h.CallerUserId = userId;
            (await Task.WhenAll(AllCommandsWithInvalidInput(h, TenantResolution.NoTenant, ct)))
                .Should().OnlyContain(r => r.Error.Code == "entitlements.actor_unknown");
            (await h.StartTrialAsync(new StartTrial(TenantA), ct)).Error.Code.Should().Be("entitlements.actor_unknown");
        }

        h.CallerUserId = TestCurrentCaller.DefaultUserId;
        (await h.GrantAsync(new GrantOverride(TenantA, FeatureKey.Create("nosuch.feature"), "pilot", null), ct)).Error.Code.Should().Be("entitlements.feature_unknown");
        (await h.GrantAsync(new GrantOverride(TenantA, FeatureKeys.ForecastingScenarios, string.Empty, null), ct)).Error.Code.Should().Be("entitlements.reason_invalid");
        (await Task.WhenAll(AllCommandsWithInvalidInput(h, TenantResolution.NoTenant, ct))).Should().OnlyContain(r => r.IsFailure);

        commands.Count.Should().Be(0, "Forbidden, then actor, then validation: none of them may touch the database");
        appends.Value.Should().Be(0);
        (await h.AuditRowsAsync(ct)).Should().BeEmpty();
        (await h.TotalRowsAsync(ct)).Should().Be(0);

        // Control: the interceptor does see a real command.
        (await h.StartTrialAsync(new StartTrial(TenantA), ct)).IsSuccess.Should().BeTrue();
        commands.Count.Should().BeGreaterThan(0);
        appends.Value.Should().Be(1);
    }

    [Trait("Category", "Integration")]
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_command_with_no_identified_caller_writes_no_entitlement_row_and_no_audit_record(string? userId)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsRoleAsync(pg, ct);
        h.CallerUserId = userId;

        var results = new[]
        {
            await h.StartTrialAsync(new StartTrial(TenantA), ct),
            await h.GrantAsync(new GrantOverride(TenantA, FeatureKeys.ForecastingScenarios, "pilot", null), ct),
            await h.RevokeAsync(new RevokeOverride(TenantA, FeatureKeys.ForecastingScenarios), ct),
        };

        results.Should().OnlyContain(r => r.Error.Code == "entitlements.actor_unknown");
        (await h.TotalRowsAsync(ct)).Should().Be(0);
        (await h.AuditRowsAsync(ct)).Should().BeEmpty();
    }
}
