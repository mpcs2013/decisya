using Decisya.TestInfrastructure;
using Microsoft.EntityFrameworkCore;

namespace Decisya.Infrastructure.Persistence.Tests;

/// <summary>
/// Two-tenant isolation tests for <see cref="TenantScopedProbe"/> (isolation-test skill,
/// issue #22): tenant A can neither read nor change tenant B's rows, including by id (BOLA).
/// </summary>
[Trait("Category", "Integration")]
public sealed class TenantScopedProbeIsolationTests
{
    private readonly PostgresFixture _pg;

    public TenantScopedProbeIsolationTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    [Fact]
    public async Task Tenant_A_cannot_read_rows_of_tenant_B()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seeded = await ProbeSeeding.SeedOneTenantAsync(_pg, "b-only-row", cancellationToken);

        await using var contextA = seeded.Db.CreateContext(seeded.Stranger);

        var rows = await contextA.Probes.ToListAsync(cancellationToken);

        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Tenant_A_cannot_update_or_delete_rows_of_tenant_B_by_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seeded = await ProbeSeeding.SeedOneTenantAsync(_pg, "b-only-row", cancellationToken);

        await using (var contextA = seeded.Db.CreateContext(seeded.Stranger))
        {
            var found = await contextA.Probes.FindAsync([seeded.ProbeId], cancellationToken);
            found.Should().BeNull("a Find by id must not cross the tenant boundary (BOLA)");

            var affectedByUpdate = await contextA.Probes
                .Where(p => p.Id == seeded.ProbeId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.Name, "tampered"), cancellationToken);
            affectedByUpdate.Should().Be(0);

            var affectedByDelete = await contextA.Probes
                .Where(p => p.Id == seeded.ProbeId)
                .ExecuteDeleteAsync(cancellationToken);
            affectedByDelete.Should().Be(0);
        }

        await using var contextB = seeded.Db.CreateContext(seeded.Owner);
        var untouched = await contextB.Probes.SingleAsync(cancellationToken);
        untouched.Name.Should().Be("b-only-row");
    }
}
