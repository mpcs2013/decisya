using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Entitlements.Domain;
using NodaTime;
using static Decisya.Modules.Entitlements.Tests.TestSupport.EntitlementsHarness;

namespace Decisya.Modules.Entitlements.Tests;

/// <summary>Fast, Docker-free checks of the catalog, the feature key and the two aggregates' activity rules (G1 Stories 1 to 3; G2 "Domain and persistence").</summary>
[Trait("Category", "Unit")]
public sealed class EntitlementsDomainTests
{
    private static readonly Duration OneTick = Duration.FromTicks(1);

    [Fact]
    public void The_default_catalog_lists_ledger_transactions_for_Free_and_Pro_and_forecasting_scenarios_for_Pro_only()
    {
        var catalog = PlanCatalog.Default;

        catalog.Includes(PlanId.Free, FeatureKeys.LedgerTransactions).Should().BeTrue();
        catalog.Includes(PlanId.Pro, FeatureKeys.LedgerTransactions).Should().BeTrue();
        catalog.Includes(PlanId.Pro, FeatureKeys.ForecastingScenarios).Should().BeTrue();
        catalog.Includes(PlanId.Free, FeatureKeys.ForecastingScenarios).Should().BeFalse();
        catalog.IsKnown(FeatureKeys.LedgerTransactions).Should().BeTrue();
        catalog.IsKnown(FeatureKeys.ForecastingScenarios).Should().BeTrue();
    }

    [Fact]
    public void The_catalog_does_not_know_an_unlisted_key_or_default_and_includes_nothing_for_them()
    {
        var catalog = PlanCatalog.Default;
        var unknown = FeatureKey.Create("nosuch.feature");

        catalog.IsKnown(unknown).Should().BeFalse();
        catalog.IsKnown(default).Should().BeFalse();
        catalog.Includes(PlanId.Pro, unknown).Should().BeFalse();
        catalog.Includes(PlanId.Pro, default).Should().BeFalse();
        catalog.Includes(PlanId.Free, default).Should().BeFalse();
        catalog.Includes((PlanId)99, FeatureKeys.LedgerTransactions).Should().BeFalse();
    }

    [Fact]
    public void A_catalog_that_lists_default_never_knows_it()
    {
        var catalog = new PlanCatalog(new Dictionary<PlanId, IReadOnlyCollection<FeatureKey>>
        {
            [PlanId.Free] = [default],
        });

        catalog.IsKnown(default).Should().BeFalse();
        catalog.Includes(PlanId.Free, default).Should().BeFalse();
    }

    [Theory]
    [InlineData("ledger.transactions", true)]
    [InlineData("forecasting.scenarios", true)]
    [InlineData("a.b", true)]
    [InlineData("a1_b.c_2", true)]
    [InlineData("", false)]
    [InlineData("nodot", false)]
    [InlineData("two.dots.here", false)]
    [InlineData(".leading", false)]
    [InlineData("trailing.", false)]
    [InlineData("Upper.case", false)]
    [InlineData("1digit.first", false)]
    [InlineData("module.1feature", false)]
    [InlineData("has space.feature", false)]
    [InlineData("hyphen-ated.feature", false)]
    [InlineData("ledger.transactions\n", false)]
    public void FeatureKey_TryCreate_accepts_only_module_dot_feature_shaped_text(string text, bool valid)
    {
        FeatureKey.TryCreate(text, out var key).Should().Be(valid);
        key.IsInitialized.Should().Be(valid);
    }

    [Fact]
    public void FeatureKey_enforces_the_64_character_limit_and_default_is_uninitialized_and_never_throws_on_ToString()
    {
        var atLimit = "m." + new string('f', FeatureKey.MaxLength - 2);

        FeatureKey.TryCreate(atLimit, out _).Should().BeTrue();
        FeatureKey.TryCreate(atLimit + "x", out _).Should().BeFalse();
        FeatureKey.TryCreate(null, out _).Should().BeFalse();

        FeatureKey uninitialized = default;
        uninitialized.IsInitialized.Should().BeFalse();
        uninitialized.ToString().Should().BeEmpty();
        var value = () => uninitialized.Value;
        value.Should().Throw<InvalidOperationException>();
        var create = () => FeatureKey.Create("Not A Key");
        create.Should().Throw<ArgumentException>();
        FeatureKey.Create("a.b").Should().Be(FeatureKey.Create("a.b"));
        FeatureKey.Create("a.b").Should().NotBe(FeatureKey.Create("a.c"));
    }

    [Fact]
    public void A_trial_lasts_exactly_14_days_of_elapsed_time_and_is_Pro()
    {
        var trial = TrialGrant.Start(TenantA, Start);

        trial.Plan.Should().Be(PlanId.Pro);
        trial.StartsAt.Should().Be(Start);
        trial.EndsAt.Should().Be(Start + Duration.FromDays(14));
        TrialGrant.TrialLength.Should().Be(Duration.FromDays(14));
        trial.TenantId.Should().Be(TenantA);
        trial.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void A_trial_is_active_from_StartsAt_up_to_but_not_including_EndsAt()
    {
        var trial = TrialGrant.Start(TenantA, Start);

        trial.IsActiveAt(Start - OneTick).Should().BeFalse();
        trial.IsActiveAt(Start).Should().BeTrue();
        trial.IsActiveAt(trial.EndsAt - OneTick).Should().BeTrue();
        trial.IsActiveAt(trial.EndsAt).Should().BeFalse();
        trial.IsActiveAt(trial.EndsAt + OneTick).Should().BeFalse();
    }

    [Fact]
    public void An_override_without_an_expiry_never_expires_and_one_with_an_expiry_is_end_exclusive()
    {
        var forever = new FeatureOverride(TenantA, FeatureKeys.ForecastingScenarios, "pilot", Start, null);
        var expiry = Start + Duration.FromDays(1);
        var limited = new FeatureOverride(TenantA, FeatureKeys.ForecastingScenarios, "pilot", Start, expiry);

        forever.IsActiveAt(Instant.MaxValue).Should().BeTrue();
        limited.IsActiveAt(expiry - OneTick).Should().BeTrue();
        limited.IsActiveAt(expiry).Should().BeFalse();
    }

    [Fact]
    public void Replacing_an_override_sets_the_reason_expiry_and_GrantedAt_and_keeps_the_identity()
    {
        var featureOverride = new FeatureOverride(TenantA, FeatureKeys.ForecastingScenarios, "first", Start, null);
        var id = featureOverride.Id;
        var later = Start + Duration.FromHours(5);

        featureOverride.Replace("second", later + Duration.FromDays(1), later);

        featureOverride.Id.Should().Be(id);
        featureOverride.TenantId.Should().Be(TenantA);
        featureOverride.Reason.Should().Be("second");
        featureOverride.GrantedAt.Should().Be(later);
        featureOverride.ExpiresAt.Should().Be(later + Duration.FromDays(1));
    }
}
