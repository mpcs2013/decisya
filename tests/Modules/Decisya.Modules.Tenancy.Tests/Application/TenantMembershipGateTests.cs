using Decisya.Modules.Tenancy.Application;
using Decisya.Modules.Tenancy.Domain;
using Decisya.Modules.Tenancy.Infrastructure;
using Decisya.Modules.Tenancy.Tests.TestSupport;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;

namespace Decisya.Modules.Tenancy.Tests.Application;

/// <summary>
/// G4-21-03 red tests for <c>TenantMembershipGate.EnsureAsync</c>, against a real Postgres
/// database (issue #21, Story 1; G3 T-05, T-06). Race behaviour (NFR-32) lives in
/// <see cref="TenantMembershipGateRaceTests"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TenantMembershipGateTests(PostgresFixture pg)
{
    private static readonly Instant Now = SystemClock.Instance.GetCurrentInstant();

    private static TenantMembershipGate CreateGate(TenancyDbContext db, TenantId t, string userId) =>
        new(db, new TestCurrentTenant { Resolution = TenantResolution.For(t) }, new StubCurrentCaller(userId), SystemClock.Instance, NullLogger<TenantMembershipGate>.Instance);

    /// <summary>Story 1 Scenario 4: "A caller whose tenant already exists but who has no membership of her own is not silently added to it."</summary>
    [Fact]
    public async Task A_caller_whose_tenant_already_exists_but_who_has_no_membership_of_her_own_is_refused_and_writes_no_row()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var t = TenantId.New();
        var db = await pg.CreateDatabaseAsync<TenancyDbContext>(cancellationToken);

        await using (var seed = db.CreateContext(t))
        {
            seed.Tenants.Add(new Tenant(t, Now));
            seed.Memberships.Add(new Membership(t, "dev-bob", TenantRole.Owner, Now));
            await seed.SaveChangesAsync(cancellationToken);
        }

        await using var ctx = db.CreateContext(t);
        var gate = CreateGate(ctx, t, "intruder-sub");

        var result = await gate.EnsureAsync(cancellationToken);

        result.Should().Be(TenantMembershipResult.Refused);

        await using var verify = db.CreateContext(t);
        (await verify.Memberships.CountAsync(cancellationToken)).Should().Be(1);
        (await verify.Memberships.AnyAsync(m => m.UserId == "intruder-sub", cancellationToken)).Should().BeFalse();
    }

    /// <summary>G3 red test: "Pre-seed a Member row for X: X gets Member (not upgraded)."</summary>
    [Fact]
    public async Task A_caller_with_an_existing_Member_role_is_recognized_as_a_member_and_never_upgraded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var t = TenantId.New();
        var db = await pg.CreateDatabaseAsync<TenancyDbContext>(cancellationToken);

        await using (var seed = db.CreateContext(t))
        {
            seed.Tenants.Add(new Tenant(t, Now));
            seed.Memberships.Add(new Membership(t, "dev-carol", TenantRole.Member, Now));
            await seed.SaveChangesAsync(cancellationToken);
        }

        await using var ctx = db.CreateContext(t);
        var gate = CreateGate(ctx, t, "dev-carol");

        var result = await gate.EnsureAsync(cancellationToken);

        result.Should().Be(TenantMembershipResult.Member);

        await using var verify = db.CreateContext(t);
        var membership = await verify.Memberships.SingleAsync(m => m.UserId == "dev-carol", cancellationToken);
        membership.Role.Should().Be(TenantRole.Member, "the gate must never upgrade an existing role");
    }

    /// <summary>
    /// #20 B-5 / Story 1 Scenario 3, defence in depth: production never calls
    /// <c>EnsureAsync</c> under <c>TenantResolution.NoTenant</c> at all — the middleware's own
    /// gating condition requires <c>Kind == Tenant</c> — but the gate itself must also fail
    /// closed if it were ever invoked with no tenant, rather than inventing a "no tenant" row.
    /// <see cref="TenantResolution.TenantId"/> throws before any <c>Add</c>/<c>SaveChangesAsync</c>
    /// call runs (see the type's own source), so this exception is itself the proof no row is
    /// written; a table read to double-check is impossible by design (a <c>None</c> resolution's
    /// query filter returns zero rows for <em>any</em> tenant, so it could never distinguish
    /// "nothing was written" from "something else's tenant exists").
    /// </summary>
    [Fact]
    public async Task A_caller_with_no_tenant_resolution_writes_nothing_even_if_the_gate_is_invoked_directly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var db = await pg.CreateDatabaseAsync<TenancyDbContext>(cancellationToken);

        var currentTenant = new TestCurrentTenant { Resolution = TenantResolution.NoTenant };
        await using var ctx = db.CreateContext(currentTenant);
        var gate = new TenantMembershipGate(ctx, currentTenant, new StubCurrentCaller("dev-admin"), SystemClock.Instance, NullLogger<TenantMembershipGate>.Instance);

        var act = () => gate.EnsureAsync(cancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
