using Decisya.Modules.Entitlements.Application;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Entitlements.Domain;
using Decisya.SharedKernel.Results;
using Decisya.TestInfrastructure;
using NodaTime;
using static Decisya.Modules.Entitlements.Tests.TestSupport.EntitlementsHarness;

namespace Decisya.Modules.Entitlements.Tests;

/// <summary>
/// G1 Story 3 plus G3 G4-23-03 (expiry boundaries at T minus one tick and at T, for trials and
/// overrides, and the 16-way StartTrial race). Real Postgres 18, <c>FakeClock</c>. Test names
/// are the Gherkin scenario titles.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EntitlementsTrialTests(PostgresFixture pg)
{
    private const int RaceCallCount = 16;

    private static readonly FeatureKey Pro = FeatureKeys.ForecastingScenarios;
    private static readonly FeatureKey Free = FeatureKeys.LedgerTransactions;
    private static readonly Duration OneTick = Duration.FromTicks(1);

    [Fact]
    public async Task Starting_a_trial_grants_Pro_features_for_exactly_14_days()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        var result = await h.StartTrialAsync(new StartTrial(TenantA), ct);

        result.IsSuccess.Should().BeTrue();
        var trial = (await h.TrialsOfAsync(TenantA, ct)).Should().ContainSingle().Which;
        trial.Plan.Should().Be(PlanId.Pro);
        trial.StartsAt.Should().Be(Instant.FromUtc(2026, 10, 1, 9, 0));
        trial.EndsAt.Should().Be(Instant.FromUtc(2026, 10, 15, 9, 0));

        h.Clock.Reset(Instant.FromUtc(2026, 10, 15, 8, 59, 59));
        (await h.IsEnabledAsync(TenantA, Pro, ct)).Should().BeTrue();
    }

    [Fact]
    public async Task The_trial_expires_at_EndsAt_and_the_tenant_reverts_to_Free()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        (await h.StartTrialAsync(new StartTrial(TenantA), ct)).IsSuccess.Should().BeTrue();

        h.Clock.Reset(Instant.FromUtc(2026, 10, 15, 9, 0));

        (await h.IsEnabledAsync(TenantA, Pro, ct)).Should().BeFalse();
        (await h.IsEnabledAsync(TenantA, Free, ct)).Should().BeTrue();
    }

    [Fact]
    public async Task A_tenant_cannot_start_a_second_trial_even_after_the_first_has_expired()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        (await h.StartTrialAsync(new StartTrial(TenantA), ct)).IsSuccess.Should().BeTrue();
        h.Clock.Reset(Instant.FromUtc(2026, 11, 1, 9, 0));

        var second = await h.StartTrialAsync(new StartTrial(TenantA), ct);

        second.IsFailure.Should().BeTrue();
        second.Error.Code.Should().Be("entitlements.trial_already_used");
        second.Error.Category.Should().Be(ErrorCategory.Conflict);
        (await h.TrialsOfAsync(TenantA, ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_trial_does_not_affect_another_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        (await h.StartTrialAsync(new StartTrial(TenantA), ct)).IsSuccess.Should().BeTrue();
        (await h.IsEnabledAsync(TenantA, Pro, ct)).Should().BeTrue();

        (await h.IsEnabledAsync(TenantB, Pro, ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Concurrent_trial_starts_for_one_tenant_create_exactly_one_TrialGrant()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        // Each call runs in its own scope with its own context, on the thread pool.
        var results = await Task.WhenAll(Enumerable.Range(0, RaceCallCount)
            .Select(_ => Task.Run(() => h.StartTrialAsync(new StartTrial(TenantA), ct), ct)));

        results.Count(r => r.IsSuccess).Should().Be(1, "exactly one start wins");
        var losers = results.Where(r => r.IsFailure).ToList();
        losers.Should().HaveCount(RaceCallCount - 1);
        losers.Should().OnlyContain(r => r.Error.Code == "entitlements.trial_already_used" && r.Error.Category == ErrorCategory.Conflict);
        (await h.TrialsOfAsync(TenantA, ct)).Should().ContainSingle();
    }

    // ---- G4-23-03: boundaries at T minus one tick and at T ----

    [Fact]
    public async Task A_trial_is_active_one_tick_before_EndsAt_and_denied_at_EndsAt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        (await h.StartTrialAsync(new StartTrial(TenantA), ct)).IsSuccess.Should().BeTrue();
        var endsAt = (await h.TrialsOfAsync(TenantA, ct)).Single().EndsAt;

        h.Clock.Reset(endsAt - OneTick);
        (await h.IsEnabledAsync(TenantA, Pro, ct)).Should().BeTrue("one tick before EndsAt the trial is still active");

        h.Clock.Reset(endsAt);
        (await h.IsEnabledAsync(TenantA, Pro, ct)).Should().BeFalse("EndsAt is exclusive");
    }

    [Fact]
    public async Task An_override_is_active_one_tick_before_ExpiresAt_and_denied_at_ExpiresAt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        var expiresAt = Start + Duration.FromDays(3);
        (await h.GrantAsync(new GrantOverride(TenantA, Pro, "pilot", expiresAt), ct)).IsSuccess.Should().BeTrue();

        h.Clock.Reset(expiresAt - OneTick);
        (await h.IsEnabledAsync(TenantA, Pro, ct)).Should().BeTrue("one tick before ExpiresAt the override is still active");

        h.Clock.Reset(expiresAt);
        (await h.IsEnabledAsync(TenantA, Pro, ct)).Should().BeFalse("ExpiresAt is exclusive");
    }

    [Fact]
    public async Task An_expired_override_with_an_active_trial_is_allowed_through_the_trial()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        (await h.GrantAsync(new GrantOverride(TenantA, Pro, "pilot", Start + Duration.FromDays(1)), ct)).IsSuccess.Should().BeTrue();
        (await h.StartTrialAsync(new StartTrial(TenantA), ct)).IsSuccess.Should().BeTrue();

        h.Clock.Reset(Start + Duration.FromDays(2));

        (await h.IsEnabledAsync(TenantA, Pro, ct)).Should().BeTrue();
    }

    [Fact]
    public async Task An_expired_trial_with_an_active_override_is_allowed_through_the_override()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        (await h.StartTrialAsync(new StartTrial(TenantA), ct)).IsSuccess.Should().BeTrue();
        (await h.GrantAsync(new GrantOverride(TenantA, Pro, "pilot", null), ct)).IsSuccess.Should().BeTrue();

        h.Clock.Reset(Start + Duration.FromDays(15));

        (await h.IsEnabledAsync(TenantA, Pro, ct)).Should().BeTrue();
    }

    [Fact]
    public async Task An_expired_trial_with_no_override_denies_the_Pro_key_and_allows_the_Free_key()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        (await h.StartTrialAsync(new StartTrial(TenantA), ct)).IsSuccess.Should().BeTrue();

        h.Clock.Reset(Start + Duration.FromDays(15));

        (await h.IsEnabledAsync(TenantA, Pro, ct)).Should().BeFalse();
        (await h.IsEnabledAsync(TenantA, Free, ct)).Should().BeTrue();
    }

    [Fact]
    public async Task A_trial_is_not_active_before_its_StartsAt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        (await h.StartTrialAsync(new StartTrial(TenantA), ct)).IsSuccess.Should().BeTrue();

        h.Clock.Reset(Start - OneTick);

        (await h.IsEnabledAsync(TenantA, Pro, ct)).Should().BeFalse();
    }
}
