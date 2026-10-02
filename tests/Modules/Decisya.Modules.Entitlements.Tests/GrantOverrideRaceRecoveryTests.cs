using Decisya.Modules.Audit.Contracts;
using Decisya.Modules.Entitlements.Application;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Entitlements.Domain;
using Decisya.Modules.Entitlements.Infrastructure;
using Decisya.Modules.Entitlements.Tests.TestSupport;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Decisya.Modules.Entitlements.Tests.TestSupport.EntitlementsHarness;

namespace Decisya.Modules.Entitlements.Tests;

/// <summary>
/// G3 S-3 (T-09): in <c>GrantOverride</c>'s 23505 path the re-read may find no row, because a
/// concurrent revoke deleted the winner. The outcome must be defined (the override is added
/// afresh), never a <see cref="NullReferenceException"/>. A <see cref="SaveChangesInterceptor"/>
/// fakes the sequence deterministically: a competing insert commits just before the handler's
/// first save (so the save raises a real Postgres 23505), and optionally the winner is deleted
/// again before the handler's re-read.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GrantOverrideRaceRecoveryTests(PostgresFixture pg)
{
    private static readonly FeatureKey Pro = FeatureKeys.ForecastingScenarios;

    // The handler is built by hand so that a SaveChangesInterceptor can sit on its context; its
    // audit writer is the real one, from the harness's DI, and asRole puts it on the real role.
    private static GrantOverrideHandler BuildHandler(
        EntitlementsHarness h, SaveChangesInterceptor interceptor, IAuditWriter audit, bool asRole)
    {
        Action<DbContextOptionsBuilder<EntitlementsDbContext>> configure = builder => builder.AddInterceptors(interceptor);
        return new(
            new TestCurrentTenant { Resolution = TenantResolution.NoTenant },
            asRole ? h.NewServiceOptions(configure) : h.NewOptions(configure),
            new TestCurrentCaller { IsPlatformAdmin = true },
            audit,
            h.TenantExistence,
            PlanCatalog.Default,
            h.Clock,
            NullLogger<GrantOverrideHandler>.Instance);
    }

    private static async Task<EntitlementsHarness> HarnessAsync(PostgresFixture pg, bool asRole, CancellationToken ct) =>
        asRole ? await CreateAsRoleAsync(pg, ct) : await CreateAsync(pg, ct);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task When_the_23505_re_read_finds_the_winner_the_handler_replaces_it_and_leaves_exactly_one_row_and_exactly_one_audit_record(bool asRole)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await HarnessAsync(pg, asRole, ct);
        using var scope = h.Scope(TenantResolution.NoTenant);
        var interceptor = new CompetingGrantInterceptor(h, deleteWinnerAfterFailure: false);
        var handler = BuildHandler(h, interceptor, scope.ServiceProvider.GetRequiredService<IAuditWriter>(), asRole);

        var result = await handler.HandleAsync(new GrantOverride(TenantA, Pro, "handler reason", null), ct);

        result.IsSuccess.Should().BeTrue();
        interceptor.CompetingInsertCount.Should().Be(1);
        interceptor.FailureCount.Should().Be(1, "the first save must have hit a real 23505");
        var row = (await h.OverridesOfAsync(TenantA, ct)).Should().ContainSingle().Which;
        row.Reason.Should().Be("handler reason");

        // One record for the one successful command: the failed first attempt left no orphan.
        var record = (await h.AuditRowsOfAsync(TenantA, ct)).Should().ContainSingle().Which;
        record.Action.Should().Be("entitlements.override.grant");
        record.FeatureKey.Should().Be(Pro.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task When_the_23505_re_read_finds_no_row_because_the_winner_was_revoked_the_override_is_added_afresh_without_a_NullReferenceException(bool asRole)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await HarnessAsync(pg, asRole, ct);
        using var scope = h.Scope(TenantResolution.NoTenant);
        var interceptor = new CompetingGrantInterceptor(h, deleteWinnerAfterFailure: true);
        var handler = BuildHandler(h, interceptor, scope.ServiceProvider.GetRequiredService<IAuditWriter>(), asRole);

        var act = () => handler.HandleAsync(new GrantOverride(TenantA, Pro, "handler reason", null), ct);

        var result = (await act.Should().NotThrowAsync()).Which;
        result.IsSuccess.Should().BeTrue();
        interceptor.FailureCount.Should().Be(1, "the first save must have hit a real 23505");
        interceptor.WinnerDeletedCount.Should().Be(1, "the winner was gone when the handler re-read");
        var row = (await h.OverridesOfAsync(TenantA, ct)).Should().ContainSingle().Which;
        row.Reason.Should().Be("handler reason");
        (await h.IsEnabledAsync(TenantA, Pro, ct)).Should().BeTrue();
        (await h.AuditRowsOfAsync(TenantA, ct)).Should().ContainSingle().Which.Action.Should().Be("entitlements.override.grant");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_second_failure_in_the_retry_propagates_instead_of_looping_and_leaves_no_audit_record(bool asRole)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await HarnessAsync(pg, asRole, ct);
        using var scope = h.Scope(TenantResolution.NoTenant);
        var interceptor = new CompetingGrantInterceptor(h, deleteWinnerAfterFailure: true, competeOnEverySave: true);
        var handler = BuildHandler(h, interceptor, scope.ServiceProvider.GetRequiredService<IAuditWriter>(), asRole);

        var act = () => handler.HandleAsync(new GrantOverride(TenantA, Pro, "handler reason", null), ct);

        await act.Should().ThrowAsync<DbUpdateException>("only one re-read and one more save are allowed");
        interceptor.FailureCount.Should().Be(2);
        (await h.AuditRowsOfAsync(TenantA, ct)).Should().BeEmpty("nothing was committed, so no record may exist");
    }

    private sealed class CompetingGrantInterceptor(
        EntitlementsHarness harness, bool deleteWinnerAfterFailure, bool competeOnEverySave = false) : SaveChangesInterceptor
    {
        private int _saving;
        private int _competing;
        private int _failures;
        private int _deleted;

        public int CompetingInsertCount => Volatile.Read(ref _competing);

        public int FailureCount => Volatile.Read(ref _failures);

        public int WinnerDeletedCount => Volatile.Read(ref _deleted);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _saving) == 1 || competeOnEverySave)
            {
                // The harness contexts carry no interceptor, so this cannot recurse. A committed
                // competing insert makes the handler's own insert fail with a real 23505.
                await using var db = harness.Context(TenantA);
                var existing = await db.FeatureOverrides.AnyAsync(cancellationToken);
                if (!existing)
                {
                    db.FeatureOverrides.Add(new FeatureOverride(TenantA, Pro, "competing winner", Start, null));
                    await db.SaveChangesAsync(cancellationToken);
                    Interlocked.Increment(ref _competing);
                }
            }

            return result;
        }

        public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _failures);

            if (deleteWinnerAfterFailure)
            {
                await using var db = harness.Context(TenantA);
                var deleted = await db.FeatureOverrides.ExecuteDeleteAsync(cancellationToken);
                if (deleted > 0)
                {
                    Interlocked.Increment(ref _deleted);
                }
            }
        }
    }
}
