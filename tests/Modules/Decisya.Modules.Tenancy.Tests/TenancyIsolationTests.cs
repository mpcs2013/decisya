using Decisya.Modules.Tenancy.Domain;
using Decisya.Modules.Tenancy.Infrastructure;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Decisya.Modules.Tenancy.Tests;

/// <summary>
/// The two-tenant isolation test the issue's own Done-when names (#21, Story 6; the
/// isolation-test skill): tenant A can neither read nor change tenant B's <see cref="Tenant"/>
/// or <see cref="Membership"/> rows, including by id (BOLA), against a real Postgres 18
/// database. Test names are the Gherkin scenario titles verbatim (CLAUDE.md).
/// </summary>
[Trait("Category", "Integration")]
public sealed class TenancyIsolationTests(PostgresFixture pg)
{
    private static readonly Instant Now = SystemClock.Instance.GetCurrentInstant();

    [Fact]
    public async Task Tenant_A_cannot_read_tenant_Bs_Tenant_row()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var a = TenantId.New();
        var b = TenantId.New();
        var db = await pg.CreateDatabaseAsync<TenancyDbContext>(cancellationToken);

        await using (var seed = db.CreateContext(b))
        {
            seed.Tenants.Add(new Tenant(b, Now));
            seed.Memberships.Add(new Membership(b, "dev-bob", TenantRole.Owner, Now));
            await seed.SaveChangesAsync(cancellationToken);
        }

        await using var contextA = db.CreateContext(a);

        // By a collection query.
        (await contextA.Tenants.ToListAsync(cancellationToken)).Should().BeEmpty();

        // By tenant B's own id.
        (await contextA.Tenants.SingleOrDefaultAsync(t => t.TenantId == b, cancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task Tenant_A_cannot_read_tenant_Bs_Membership_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var a = TenantId.New();
        var b = TenantId.New();
        var db = await pg.CreateDatabaseAsync<TenancyDbContext>(cancellationToken);

        Guid membershipId;
        await using (var seed = db.CreateContext(b))
        {
            seed.Tenants.Add(new Tenant(b, Now));
            var membership = new Membership(b, "dev-bob", TenantRole.Owner, Now);
            seed.Memberships.Add(membership);
            await seed.SaveChangesAsync(cancellationToken);
            membershipId = membership.Id;
        }

        await using var contextA = db.CreateContext(a);

        // By a collection query.
        (await contextA.Memberships.ToListAsync(cancellationToken)).Should().BeEmpty();

        // By tenant B's membership's own id.
        (await contextA.Memberships.SingleOrDefaultAsync(m => m.Id == membershipId, cancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task Tenant_A_cannot_update_or_delete_tenant_Bs_Membership_row_by_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var a = TenantId.New();
        var b = TenantId.New();
        var db = await pg.CreateDatabaseAsync<TenancyDbContext>(cancellationToken);

        Guid membershipId;
        await using (var seed = db.CreateContext(b))
        {
            seed.Tenants.Add(new Tenant(b, Now));
            var membership = new Membership(b, "dev-bob", TenantRole.Owner, Now);
            seed.Memberships.Add(membership);
            await seed.SaveChangesAsync(cancellationToken);
            membershipId = membership.Id;
        }

        await using (var contextA = db.CreateContext(a))
        {
            // A bulk ExecuteUpdate/ExecuteDelete still applies TenantDbContext's own query
            // filter (it is not on CrossTenantQueryRule's bypass list), so — exactly like the
            // #22 precedent (TenantScopedProbeIsolationTests) — "tenant B's row, by id" combined
            // with "tenant A's ambient filter" can never match any row: zero rows affected,
            // never an exception, and no read-back of tenant B's data is ever needed to prove it.
            var affectedByUpdate = await contextA.Memberships
                .Where(m => m.Id == membershipId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.Role, TenantRole.Member), cancellationToken);
            affectedByUpdate.Should().Be(0);

            var affectedByDelete = await contextA.Memberships
                .Where(m => m.Id == membershipId)
                .ExecuteDeleteAsync(cancellationToken);
            affectedByDelete.Should().Be(0);
        }

        await using var contextB = db.CreateContext(b);
        var unchanged = await contextB.Memberships.SingleAsync(m => m.Id == membershipId, cancellationToken);
        unchanged.UserId.Should().Be("dev-bob");
        unchanged.Role.Should().Be(TenantRole.Owner);
    }
}
