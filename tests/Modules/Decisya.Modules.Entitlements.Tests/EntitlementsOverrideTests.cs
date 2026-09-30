using Decisya.Modules.Entitlements.Application;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Entitlements.Domain;
using Decisya.SharedKernel.Results;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using NodaTime;
using static Decisya.Modules.Entitlements.Tests.TestSupport.EntitlementsHarness;

namespace Decisya.Modules.Entitlements.Tests;

/// <summary>
/// G1 Stories 1 and 2 (the issue's Done-when): a Free tenant is denied the Phase 3 feature and an
/// admin override grants it. Test names are the Gherkin scenario titles. Real Postgres 18,
/// <c>FakeClock</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EntitlementsOverrideTests(PostgresFixture pg)
{
    private static FeatureKey Pro => FeatureKeys.ForecastingScenarios;

    private static GrantOverride Grant(TenantId tenant, string reason = "Design-partner pilot", Instant? expiresAt = null, FeatureKey? feature = null) =>
        new(tenant, feature ?? Pro, reason, expiresAt);

    // ---- Story 1 ----

    [Fact]
    public async Task A_tenant_with_no_trial_and_no_override_is_on_Free_and_is_denied_the_Phase_3_feature()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        (await h.IsEnabledAsync(TenantA, Pro, ct)).Should().BeFalse();
    }

    [Fact]
    public async Task A_Free_tenant_is_allowed_a_feature_the_Free_plan_includes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        (await h.IsEnabledAsync(TenantA, FeatureKeys.LedgerTransactions, ct)).Should().BeTrue();
    }

    [Fact]
    public async Task An_unknown_feature_key_is_denied()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        var act = () => h.IsEnabledAsync(TenantA, FeatureKey.Create("nosuch.feature"), ct);

        (await act.Should().NotThrowAsync()).Which.Should().BeFalse();
    }

    // ---- Story 2 ----

    [Fact]
    public async Task An_admin_override_grants_the_Phase_3_feature()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        (await h.IsEnabledAsync(TenantA, Pro, ct)).Should().BeFalse("the Free plan denies the Phase 3 feature before the override");

        var result = await h.GrantAsync(Grant(TenantA), ct);

        result.IsSuccess.Should().BeTrue();
        var rows = await h.OverridesOfAsync(TenantA, ct);
        var row = rows.Should().ContainSingle().Which;
        row.FeatureKey.Should().Be(Pro);
        row.GrantedAt.Should().Be(Start);
        row.ExpiresAt.Should().BeNull();
        (await h.IsEnabledAsync(TenantA, Pro, ct)).Should().BeTrue();
    }

    [Fact]
    public async Task An_override_only_affects_the_tenant_and_feature_it_names()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        (await h.GrantAsync(Grant(TenantA), ct)).IsSuccess.Should().BeTrue();

        // The real catalog has no second Pro-only key, so the internal catalog seam supplies one.
        var otherProOnly = FeatureKey.Create("reporting.exports");
        var catalog = new PlanCatalog(new Dictionary<PlanId, IReadOnlyCollection<FeatureKey>>
        {
            [PlanId.Free] = [FeatureKeys.LedgerTransactions],
            [PlanId.Pro] = [FeatureKeys.LedgerTransactions, Pro, otherProOnly],
        });

        (await h.IsEnabledWithCatalogAsync(TenantB, Pro, catalog, ct)).Should().BeFalse("tenant B has no override");
        (await h.IsEnabledWithCatalogAsync(TenantA, otherProOnly, catalog, ct)).Should().BeFalse("the override names only forecasting.scenarios");
        (await h.IsEnabledWithCatalogAsync(TenantA, Pro, catalog, ct)).Should().BeTrue();
    }

    [Fact]
    public async Task An_override_with_an_expiry_is_honoured_until_the_expiry_instant_and_not_after()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        var expiresAt = Instant.FromUtc(2026, 10, 15, 9, 0);
        (await h.GrantAsync(Grant(TenantA, expiresAt: expiresAt), ct)).IsSuccess.Should().BeTrue();

        h.Clock.Reset(Instant.FromUtc(2026, 10, 15, 8, 59, 59));
        (await h.IsEnabledAsync(TenantA, Pro, ct)).Should().BeTrue();

        h.Clock.Reset(expiresAt);
        (await h.IsEnabledAsync(TenantA, Pro, ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Granting_again_replaces_the_existing_override_never_duplicating_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        (await h.GrantAsync(Grant(TenantA, "Design-partner pilot"), ct)).IsSuccess.Should().BeTrue();

        h.Clock.Advance(Duration.FromHours(1));
        (await h.GrantAsync(Grant(TenantA, "Pilot extended"), ct)).IsSuccess.Should().BeTrue();

        var row = (await h.OverridesOfAsync(TenantA, ct)).Should().ContainSingle().Which;
        row.Reason.Should().Be("Pilot extended");
        row.GrantedAt.Should().Be(Start + Duration.FromHours(1));
    }

    [Fact]
    public async Task Revoking_an_override_restores_the_plans_answer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        (await h.GrantAsync(Grant(TenantA), ct)).IsSuccess.Should().BeTrue();
        (await h.IsEnabledAsync(TenantA, Pro, ct)).Should().BeTrue();

        var revoke = await h.RevokeAsync(new RevokeOverride(TenantA, Pro), ct);

        revoke.IsSuccess.Should().BeTrue();
        (await h.IsEnabledAsync(TenantA, Pro, ct)).Should().BeFalse();
        (await h.OverridesOfAsync(TenantA, ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Revoking_a_feature_that_has_no_override_succeeds_without_error_and_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        (await h.GrantAsync(Grant(TenantB), ct)).IsSuccess.Should().BeTrue();
        var before = await h.OverridesOfAsync(TenantB, ct);

        var revoke = await h.RevokeAsync(new RevokeOverride(TenantA, Pro), ct);

        revoke.IsSuccess.Should().BeTrue();
        (await h.TotalRowsAsync(ct)).Should().Be(1);
        (await h.OverridesOfAsync(TenantB, ct)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task An_override_for_an_unknown_feature_key_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        var result = await h.GrantAsync(Grant(TenantA, feature: FeatureKey.Create("nosuch.feature")), ct);
        var defaultKey = await h.GrantAsync(Grant(TenantA, feature: default(FeatureKey)), ct);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("entitlements.feature_unknown");
        result.Error.Category.Should().Be(ErrorCategory.Validation);
        defaultKey.Error.Code.Should().Be("entitlements.feature_unknown");
        (await h.TotalRowsAsync(ct)).Should().Be(0);
    }

    [Fact]
    public async Task An_override_needs_a_reason_and_an_expiry_in_the_future()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        var empty = await h.GrantAsync(Grant(TenantA, reason: string.Empty), ct);
        var blank = await h.GrantAsync(Grant(TenantA, reason: "   "), ct);
        var tooLong = await h.GrantAsync(Grant(TenantA, reason: new string('x', FeatureOverride.MaxReasonLength + 1)), ct);
        var inThePast = await h.GrantAsync(Grant(TenantA, expiresAt: Instant.FromUtc(2026, 9, 30, 9, 0)), ct);
        var atNow = await h.GrantAsync(Grant(TenantA, expiresAt: Start), ct);

        foreach (var reason in new[] { empty, blank, tooLong })
        {
            reason.IsFailure.Should().BeTrue();
            reason.Error.Code.Should().Be("entitlements.reason_invalid");
            reason.Error.Category.Should().Be(ErrorCategory.Validation);
        }

        foreach (var expiry in new[] { inThePast, atNow })
        {
            expiry.IsFailure.Should().BeTrue();
            expiry.Error.Code.Should().Be("entitlements.expiry_not_in_future");
            expiry.Error.Category.Should().Be(ErrorCategory.Validation);
        }

        (await h.TotalRowsAsync(ct)).Should().Be(0);
    }

    [Fact]
    public async Task A_reason_of_exactly_500_characters_is_accepted_and_is_stored_trimmed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        var atLimit = await h.GrantAsync(Grant(TenantA, reason: "  " + new string('x', FeatureOverride.MaxReasonLength) + "  "), ct);

        atLimit.IsSuccess.Should().BeTrue();
        (await h.OverridesOfAsync(TenantA, ct)).Single().Reason.Should().HaveLength(FeatureOverride.MaxReasonLength);
    }

    [Fact]
    public async Task A_default_target_tenant_is_rejected_as_tenant_invalid_without_an_exception()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        var grant = await h.GrantAsync(Grant(default), ct);
        var revoke = await h.RevokeAsync(new RevokeOverride(default, Pro), ct);
        var trial = await h.StartTrialAsync(new StartTrial(default), ct);

        foreach (var result in new[] { grant, revoke, trial })
        {
            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be("entitlements.tenant_invalid");
            result.Error.Category.Should().Be(ErrorCategory.Validation);
        }
    }

    [Fact]
    public async Task Revoking_an_override_for_a_key_later_removed_from_the_catalog_still_works()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        var legacy = FeatureKey.Create("legacy.feature");
        await using (var seed = h.Context(TenantA))
        {
            seed.FeatureOverrides.Add(new FeatureOverride(TenantA, legacy, "from an older catalog", Start, null));
            await seed.SaveChangesAsync(ct);
        }

        var revoke = await h.RevokeAsync(new RevokeOverride(TenantA, legacy), ct);

        revoke.IsSuccess.Should().BeTrue();
        (await h.OverridesOfAsync(TenantA, ct)).Should().BeEmpty();
    }
}
