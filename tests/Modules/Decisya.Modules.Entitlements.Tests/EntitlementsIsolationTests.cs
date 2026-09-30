using Decisya.Infrastructure.Persistence;
using Decisya.Modules.Entitlements.Application;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Entitlements.Domain;
using Decisya.Modules.Entitlements.Tests.TestSupport;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using static Decisya.Modules.Entitlements.Tests.TestSupport.EntitlementsHarness;

namespace Decisya.Modules.Entitlements.Tests;

/// <summary>
/// G1 Story 4, NFR-34 and G3 G4-23-01 (the isolation-test skill): tenant A can neither read nor
/// change tenant B's <see cref="TrialGrant"/> or <see cref="FeatureOverride"/> rows, by
/// collection or by id, and an admin command for A leaves B byte-identical. Real Postgres 18.
/// Test names are the Gherkin scenario titles.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EntitlementsIsolationTests(PostgresFixture pg)
{
    private static readonly FeatureKey Pro = FeatureKeys.ForecastingScenarios;

    private static async Task<(Guid TrialId, Guid OverrideId)> SeedAsync(
        EntitlementsHarness h, TenantId tenant, CancellationToken ct)
    {
        await using var db = h.Context(tenant);
        var trial = TrialGrant.Start(tenant, Start);
        var featureOverride = new FeatureOverride(tenant, Pro, $"seed for {tenant}", Start, null);
        db.TrialGrants.Add(trial);
        db.FeatureOverrides.Add(featureOverride);
        await db.SaveChangesAsync(ct);
        return (trial.Id, featureOverride.Id);
    }

    [Fact]
    public async Task Tenant_A_cannot_read_tenant_Bs_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        var a = await SeedAsync(h, TenantA, ct);
        var b = await SeedAsync(h, TenantB, ct);

        await using var contextA = h.Context(TenantA);

        // By a collection query: only A's own rows.
        (await contextA.TrialGrants.Select(t => t.Id).ToListAsync(ct)).Should().Equal(a.TrialId);
        (await contextA.FeatureOverrides.Select(o => o.Id).ToListAsync(ct)).Should().Equal(a.OverrideId);

        // By tenant B's own ids.
        (await contextA.TrialGrants.SingleOrDefaultAsync(t => t.Id == b.TrialId, ct)).Should().BeNull();
        (await contextA.FeatureOverrides.SingleOrDefaultAsync(o => o.Id == b.OverrideId, ct)).Should().BeNull();
    }

    [Fact]
    public async Task Tenant_A_cannot_update_or_delete_tenant_Bs_rows_by_id()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        await SeedAsync(h, TenantA, ct);
        var b = await SeedAsync(h, TenantB, ct);
        var before = await h.OverridesOfAsync(TenantB, ct);
        var trialBefore = await h.TrialsOfAsync(TenantB, ct);

        await using (var contextA = h.Context(TenantA))
        {
            (await contextA.FeatureOverrides.Where(o => o.Id == b.OverrideId)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Reason, "tampered"), ct)).Should().Be(0);
            (await contextA.TrialGrants.Where(t => t.Id == b.TrialId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.EndsAt, Start + Duration.FromDays(900)), ct)).Should().Be(0);
            (await contextA.FeatureOverrides.Where(o => o.Id == b.OverrideId).ExecuteDeleteAsync(ct)).Should().Be(0);
            (await contextA.TrialGrants.Where(t => t.Id == b.TrialId).ExecuteDeleteAsync(ct)).Should().Be(0);
        }

        (await h.OverridesOfAsync(TenantB, ct)).Should().BeEquivalentTo(before);
        (await h.TrialsOfAsync(TenantB, ct)).Should().BeEquivalentTo(trialBefore);
    }

    [Fact]
    public async Task Tenant_A_cannot_save_a_row_that_belongs_to_tenant_B()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        await using (var contextA = h.Context(TenantA))
        {
            contextA.TrialGrants.Add(TrialGrant.Start(TenantB, Start));
            var act = () => contextA.SaveChangesAsync(ct);

            var failure = await act.Should().ThrowAsync<TenantIsolationException>();
            failure.Which.Violation.Should().Be(TenantIsolationViolation.TenantMismatch);
        }

        (await h.TotalRowsAsync(ct)).Should().Be(0);
    }

    [Fact]
    public async Task An_evaluation_never_reflects_another_tenants_override_or_trial()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        await SeedAsync(h, TenantB, ct);

        (await h.IsEnabledAsync(TenantB, Pro, ct)).Should().BeTrue();
        (await h.IsEnabledAsync(TenantA, Pro, ct)).Should().BeFalse();
        (await h.IsEnabledAsync(TenantC, Pro, ct)).Should().BeFalse();
    }

    [Fact]
    public async Task The_module_persists_only_through_ITenantScoped_aggregates()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        await using var db = h.Context(TenantA);

        var entityTypes = db.Model.GetEntityTypes().ToList();

        entityTypes.Should().HaveCount(2);
        entityTypes.Should().OnlyContain(e => typeof(ITenantScoped).IsAssignableFrom(e.ClrType));
        entityTypes.Select(e => e.GetSchema()).Should().OnlyContain(s => s == "entitlements");
        entityTypes.Select(e => e.GetTableName()).Should().BeEquivalentTo("trial_grants", "feature_overrides");
    }

    // ---- G4-23-01: a command for one tenant changes no row of another ----

    [Fact]
    public async Task A_command_for_tenant_A_changes_no_row_of_tenant_B()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);

        // B and C are seeded through the commands themselves, at the start instant.
        foreach (var tenant in new[] { TenantB, TenantC })
        {
            (await h.StartTrialAsync(new StartTrial(tenant), ct)).IsSuccess.Should().BeTrue();
            (await h.GrantAsync(new GrantOverride(tenant, Pro, $"seed {tenant}", null), ct)).IsSuccess.Should().BeTrue();
        }

        var bOverridesBefore = await h.OverridesOfAsync(TenantB, ct);
        var bTrialsBefore = await h.TrialsOfAsync(TenantB, ct);
        var cOverridesBefore = await h.OverridesOfAsync(TenantC, ct);
        var cTrialsBefore = await h.TrialsOfAsync(TenantC, ct);

        // Tenant A gets its own rows, then a replacement, a conflicting trial start and a revoke, later in time.
        h.Clock.Advance(Duration.FromHours(2));
        (await h.StartTrialAsync(new StartTrial(TenantA), ct)).IsSuccess.Should().BeTrue();
        (await h.StartTrialAsync(new StartTrial(TenantA), ct)).IsFailure.Should().BeTrue();
        (await h.GrantAsync(new GrantOverride(TenantA, Pro, "first", null), ct)).IsSuccess.Should().BeTrue();
        (await h.GrantAsync(new GrantOverride(TenantA, Pro, "replaced", Start + Duration.FromDays(30)), ct)).IsSuccess.Should().BeTrue();
        (await h.OverridesOfAsync(TenantA, ct)).Should().ContainSingle().Which.Reason.Should().Be("replaced");
        (await h.RevokeAsync(new RevokeOverride(TenantA, Pro), ct)).IsSuccess.Should().BeTrue();
        (await h.OverridesOfAsync(TenantA, ct)).Should().BeEmpty();

        (await h.OverridesOfAsync(TenantB, ct)).Should().BeEquivalentTo(bOverridesBefore);
        (await h.TrialsOfAsync(TenantB, ct)).Should().BeEquivalentTo(bTrialsBefore);
        (await h.OverridesOfAsync(TenantC, ct)).Should().BeEquivalentTo(cOverridesBefore);
        (await h.TrialsOfAsync(TenantC, ct)).Should().BeEquivalentTo(cTrialsBefore);
    }

    [Fact]
    public async Task A_revoke_for_tenant_A_does_not_delete_tenant_Bs_override_for_the_same_feature()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(pg, ct);
        (await h.GrantAsync(new GrantOverride(TenantB, Pro, "b", null), ct)).IsSuccess.Should().BeTrue();

        (await h.RevokeAsync(new RevokeOverride(TenantA, Pro), ct)).IsSuccess.Should().BeTrue();

        (await h.OverridesOfAsync(TenantB, ct)).Should().ContainSingle();
        (await h.IsEnabledAsync(TenantB, Pro, ct)).Should().BeTrue();
    }
}
