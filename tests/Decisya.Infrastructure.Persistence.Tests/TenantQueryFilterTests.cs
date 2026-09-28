using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.EntityFrameworkCore;

namespace Decisya.Infrastructure.Persistence.Tests;

/// <summary>
/// Issue #22, G4-22-01, against a real Postgres 18 container (<see cref="PostgresFixture"/>):
/// a <c>None</c> resolution yields zero rows — never every tenant's rows — for a collection
/// query, <c>FindAsync</c>, <c>CountAsync</c>, <c>ExecuteUpdateAsync</c> and
/// <c>ExecuteDeleteAsync</c>, against two seeded tenants; and the tenant filter is evaluated
/// per query, never cached into the compiled model shared by every <see cref="ProbeDbContext"/>
/// instance — proved with two separate contexts and with an <c>EF.CompileAsyncQuery</c>
/// delegate run against both.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TenantQueryFilterTests
{
    private static readonly Func<ProbeDbContext, IAsyncEnumerable<string>> CompiledProbeNamesQuery =
        EF.CompileAsyncQuery((ProbeDbContext context) => context.Probes.Select(p => p.Name));

    private readonly PostgresFixture _pg;

    public TenantQueryFilterTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    [Fact]
    public async Task With_a_None_resolution_a_collection_query_returns_zero_rows_against_two_seeded_tenants()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seeded = await ProbeSeeding.SeedTwoTenantsAsync(_pg, cancellationToken);

        await using var context = seeded.Db.CreateContext(new TestCurrentTenant { Resolution = TenantResolution.NoTenant });

        var rows = await context.Probes.ToListAsync(cancellationToken);

        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task With_a_None_resolution_FindAsync_returns_null_for_a_row_seeded_under_a_tenant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seeded = await ProbeSeeding.SeedTwoTenantsAsync(_pg, cancellationToken);

        await using var context = seeded.Db.CreateContext(new TestCurrentTenant { Resolution = TenantResolution.NoTenant });

        var found = await context.Probes.FindAsync([seeded.ProbeAId], cancellationToken);

        found.Should().BeNull();
    }

    [Fact]
    public async Task With_a_None_resolution_CountAsync_returns_zero_against_two_seeded_tenants()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seeded = await ProbeSeeding.SeedTwoTenantsAsync(_pg, cancellationToken);

        await using var context = seeded.Db.CreateContext(new TestCurrentTenant { Resolution = TenantResolution.NoTenant });

        var count = await context.Probes.CountAsync(cancellationToken);

        count.Should().Be(0);
    }

    [Fact]
    public async Task With_a_None_resolution_ExecuteUpdateAsync_affects_zero_rows_against_two_seeded_tenants()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seeded = await ProbeSeeding.SeedTwoTenantsAsync(_pg, cancellationToken);

        await using var context = seeded.Db.CreateContext(new TestCurrentTenant { Resolution = TenantResolution.NoTenant });

        var affected = await context.Probes.ExecuteUpdateAsync(
            setters => setters.SetProperty(p => p.Name, "moved"), cancellationToken);

        affected.Should().Be(0);
    }

    [Fact]
    public async Task With_a_None_resolution_ExecuteDeleteAsync_affects_zero_rows_against_two_seeded_tenants()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seeded = await ProbeSeeding.SeedTwoTenantsAsync(_pg, cancellationToken);

        await using var context = seeded.Db.CreateContext(new TestCurrentTenant { Resolution = TenantResolution.NoTenant });

        var affected = await context.Probes.ExecuteDeleteAsync(cancellationToken);

        affected.Should().Be(0);
    }

    [Fact]
    public async Task Two_contexts_of_the_same_type_each_see_only_their_own_tenants_row()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seeded = await ProbeSeeding.SeedTwoTenantsAsync(_pg, cancellationToken);

        await using var contextA = seeded.Db.CreateContext(seeded.TenantA);
        await using var contextB = seeded.Db.CreateContext(seeded.TenantB);

        var rowsSeenByA = await contextA.Probes.ToListAsync(cancellationToken);
        var rowsSeenByB = await contextB.Probes.ToListAsync(cancellationToken);

        rowsSeenByA.Should().ContainSingle(p => p.Name == "a-row");
        rowsSeenByB.Should().ContainSingle(p => p.Name == "b-row");
    }

    [Fact]
    public async Task A_compiled_async_query_delegate_run_against_two_contexts_returns_only_each_ones_own_tenant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seeded = await ProbeSeeding.SeedTwoTenantsAsync(_pg, cancellationToken);

        await using var contextA = seeded.Db.CreateContext(seeded.TenantA);
        await using var contextB = seeded.Db.CreateContext(seeded.TenantB);

        var namesSeenByA = new List<string>();
        await foreach (var name in CompiledProbeNamesQuery(contextA).WithCancellation(cancellationToken))
        {
            namesSeenByA.Add(name);
        }

        var namesSeenByB = new List<string>();
        await foreach (var name in CompiledProbeNamesQuery(contextB).WithCancellation(cancellationToken))
        {
            namesSeenByB.Add(name);
        }

        namesSeenByA.Should().Equal("a-row");
        namesSeenByB.Should().Equal("b-row");
    }
}
