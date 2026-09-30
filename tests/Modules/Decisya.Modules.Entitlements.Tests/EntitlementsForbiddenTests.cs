using Decisya.Modules.Entitlements.Application;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.SharedKernel.Results;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.Extensions.Logging;
using NodaTime;
using static Decisya.Modules.Entitlements.Tests.TestSupport.EntitlementsHarness;

namespace Decisya.Modules.Entitlements.Tests;

/// <summary>
/// G3 G4-23-01: the ambient precondition. Each admin command's first statement requires an
/// ambient resolution of <c>None</c> (a platform admin). <c>Tenant</c> and <c>Invalid</c> get
/// <c>entitlements.forbidden</c> before validation, before any context exists and before any
/// row is written. The unit tests use a placeholder host, so any database access would throw.
/// </summary>
public sealed class EntitlementsForbiddenTests(PostgresFixture pg)
{
    public static TheoryData<string> NonAdminAmbients => new() { "Tenant", "Invalid" };

    private static TenantResolution Ambient(string name) =>
        name == "Tenant" ? TenantResolution.For(TenantA) : TenantResolution.Invalid;

    /// <summary>The commands carry an invalid feature, an empty reason and no initialized tenant: only a precondition that runs first can produce "forbidden".</summary>
    [Trait("Category", "Unit")]
    [Theory]
    [MemberData(nameof(NonAdminAmbients))]
    public async Task A_non_admin_ambient_gets_forbidden_from_every_command_before_validation_and_with_no_database_access(string ambientName)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = CreateUnreachable();
        var ambient = Ambient(ambientName);

        var trial = await h.StartTrialAsync(new StartTrial(default), ct, ambient);
        var grant = await h.GrantAsync(new GrantOverride(default, default, string.Empty, Instant.MinValue), ct, ambient);
        var revoke = await h.RevokeAsync(new RevokeOverride(default, default), ct, ambient);

        foreach (var result in new[] { trial, grant, revoke })
        {
            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("entitlements.forbidden");
            result.Error.Category.Should().Be(ErrorCategory.Forbidden);
        }

        var warnings = h.Logs.Records.Where(r => r.Level == LogLevel.Warning).ToList();
        warnings.Should().HaveCount(3);
        warnings.Should().OnlyContain(r => r.Contains(ambientName));
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task A_tenant_less_admin_ambient_with_a_default_target_tenant_gets_tenant_invalid_and_no_exception()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = CreateUnreachable();

        var trial = await h.StartTrialAsync(new StartTrial(default), ct);
        var grant = await h.GrantAsync(new GrantOverride(default, FeatureKeys.ForecastingScenarios, "pilot", null), ct);
        var revoke = await h.RevokeAsync(new RevokeOverride(default, FeatureKeys.ForecastingScenarios), ct);

        foreach (var result in new[] { trial, grant, revoke })
        {
            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("entitlements.tenant_invalid");
        }
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task Validation_fails_before_any_database_access()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = CreateUnreachable();

        var unknownFeature = await h.GrantAsync(new GrantOverride(TenantA, FeatureKey.Create("nosuch.feature"), "pilot", null), ct);
        var emptyReason = await h.GrantAsync(new GrantOverride(TenantA, FeatureKeys.ForecastingScenarios, string.Empty, null), ct);
        var pastExpiry = await h.GrantAsync(new GrantOverride(TenantA, FeatureKeys.ForecastingScenarios, "pilot", Start - Duration.FromDays(1)), ct);
        var defaultRevokeKey = await h.RevokeAsync(new RevokeOverride(TenantA, default), ct);

        unknownFeature.Error.Code.Should().Be("entitlements.feature_unknown");
        emptyReason.Error.Code.Should().Be("entitlements.reason_invalid");
        pastExpiry.Error.Code.Should().Be("entitlements.expiry_not_in_future");
        defaultRevokeKey.Error.Code.Should().Be("entitlements.feature_unknown");
    }

    [Trait("Category", "Integration")]
    [Theory]
    [MemberData(nameof(NonAdminAmbients))]
    public async Task A_non_admin_ambient_writes_no_row_for_any_command_against_any_tenant(string ambientName)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        var ambient = Ambient(ambientName);

        foreach (var target in new[] { TenantA, TenantB })
        {
            var trial = await h.StartTrialAsync(new StartTrial(target), ct, ambient);
            var grant = await h.GrantAsync(new GrantOverride(target, FeatureKeys.ForecastingScenarios, "valid reason", null), ct, ambient);
            var revoke = await h.RevokeAsync(new RevokeOverride(target, FeatureKeys.ForecastingScenarios), ct, ambient);

            foreach (var result in new[] { trial, grant, revoke })
            {
                result.Error.Code.Should().Be("entitlements.forbidden");
            }
        }

        (await h.TotalRowsAsync(ct)).Should().Be(0);
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task A_non_admin_ambient_cannot_revoke_an_existing_override_or_replace_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        (await h.GrantAsync(new GrantOverride(TenantA, FeatureKeys.ForecastingScenarios, "original", null), ct)).IsSuccess.Should().BeTrue();
        var before = await h.OverridesOfAsync(TenantA, ct);

        var tenantCaller = TenantResolution.For(TenantA);
        (await h.RevokeAsync(new RevokeOverride(TenantA, FeatureKeys.ForecastingScenarios), ct, tenantCaller)).Error.Code.Should().Be("entitlements.forbidden");
        (await h.GrantAsync(new GrantOverride(TenantA, FeatureKeys.ForecastingScenarios, "replaced", null), ct, tenantCaller)).Error.Code.Should().Be("entitlements.forbidden");

        (await h.OverridesOfAsync(TenantA, ct)).Should().BeEquivalentTo(before);
    }
}
