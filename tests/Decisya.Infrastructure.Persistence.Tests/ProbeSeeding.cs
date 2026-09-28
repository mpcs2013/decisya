using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;

namespace Decisya.Infrastructure.Persistence.Tests;

/// <summary>
/// Shared seeding helpers for the #22 Postgres-backed tenant isolation tests. Every call gets
/// a fresh database (<see cref="PostgresFixture.CreateDatabaseAsync{TContext}"/> is a database
/// per call, not a schema per test), holding exactly the rows the calling test needs — tests
/// never share rows with each other.
/// </summary>
internal static class ProbeSeeding
{
    /// <summary>Seeds one row for a freshly minted tenant A and one row for a freshly minted tenant B, in a fresh database.</summary>
    public static async Task<TwoTenantProbes> SeedTwoTenantsAsync(PostgresFixture pg, CancellationToken cancellationToken)
    {
        var db = await pg.CreateDatabaseAsync<ProbeDbContext>(cancellationToken).ConfigureAwait(false);
        var tenantA = TenantId.New();
        var tenantB = TenantId.New();

        var probeAId = await SeedOneAsync(db, tenantA, "a-row", cancellationToken).ConfigureAwait(false);
        var probeBId = await SeedOneAsync(db, tenantB, "b-row", cancellationToken).ConfigureAwait(false);

        return new TwoTenantProbes(db, tenantA, tenantB, probeAId, probeBId);
    }

    /// <summary>Seeds one row for a freshly minted owning tenant, in a fresh database; a second, freshly minted tenant (<see cref="OneTenantProbe.Stranger"/>) never owns any row here.</summary>
    public static async Task<OneTenantProbe> SeedOneTenantAsync(PostgresFixture pg, string name, CancellationToken cancellationToken)
    {
        var db = await pg.CreateDatabaseAsync<ProbeDbContext>(cancellationToken).ConfigureAwait(false);
        var owner = TenantId.New();
        var stranger = TenantId.New();

        var probeId = await SeedOneAsync(db, owner, name, cancellationToken).ConfigureAwait(false);

        return new OneTenantProbe(db, owner, stranger, probeId);
    }

    private static async Task<Guid> SeedOneAsync(
        PostgresTestDatabase<ProbeDbContext> db, TenantId tenant, string name, CancellationToken cancellationToken)
    {
        await using var context = db.CreateContext(tenant);
        var probe = new TenantScopedProbe(tenant, name);
        context.Probes.Add(probe);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return probe.Id;
    }
}

internal sealed record TwoTenantProbes(
    PostgresTestDatabase<ProbeDbContext> Db,
    TenantId TenantA,
    TenantId TenantB,
    Guid ProbeAId,
    Guid ProbeBId);

internal sealed record OneTenantProbe(
    PostgresTestDatabase<ProbeDbContext> Db,
    TenantId Owner,
    TenantId Stranger,
    Guid ProbeId);
