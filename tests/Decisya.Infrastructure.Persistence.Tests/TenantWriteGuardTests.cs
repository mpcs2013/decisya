using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.EntityFrameworkCore;

namespace Decisya.Infrastructure.Persistence.Tests;

/// <summary>
/// Issue #22, G4-22-02, against a real Postgres 18 container (<see cref="PostgresFixture"/>):
/// a same-tenant write persists and reads back; a detached <c>Update</c> or <c>Remove</c>
/// carrying tenant B's id, run as tenant A, raises <see cref="DbUpdateConcurrencyException"/>
/// because <c>TenantId</c> is a concurrency token (T-05), and leaves tenant B's row unchanged.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TenantWriteGuardTests
{
    private readonly PostgresFixture _pg;

    public TenantWriteGuardTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    [Fact]
    public async Task A_same_tenant_write_persists_and_is_read_back_under_the_same_tenant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var db = await _pg.CreateDatabaseAsync<ProbeDbContext>(cancellationToken);
        var tenantA = TenantId.New();

        await using (var writeContext = db.CreateContext(tenantA))
        {
            writeContext.Probes.Add(new TenantScopedProbe(tenantA, "mine"));
            await writeContext.SaveChangesAsync(cancellationToken);
        }

        await using var readContext = db.CreateContext(tenantA);
        var rows = await readContext.Probes.ToListAsync(cancellationToken);

        rows.Should().ContainSingle(p => p.Name == "mine");
    }

    [Fact]
    public async Task A_detached_Update_carrying_tenant_Bs_id_run_as_tenant_A_raises_a_concurrency_exception_and_leaves_Bs_row_unchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seeded = await ProbeSeeding.SeedOneTenantAsync(_pg, "b-row", cancellationToken);

        await using (var contextA = seeded.Db.CreateContext(seeded.Stranger))
        {
            var stub = new TenantScopedProbe(seeded.ProbeId, seeded.Stranger, "tampered");
            contextA.Probes.Update(stub);

            var act = async () => await contextA.SaveChangesAsync(cancellationToken);

            await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        }

        await using var contextB = seeded.Db.CreateContext(seeded.Owner);
        var untouched = await contextB.Probes.SingleAsync(cancellationToken);
        untouched.Name.Should().Be("b-row");
    }

    [Fact]
    public async Task A_detached_Remove_carrying_tenant_Bs_id_run_as_tenant_A_raises_a_concurrency_exception_and_leaves_Bs_row_unchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seeded = await ProbeSeeding.SeedOneTenantAsync(_pg, "b-row", cancellationToken);

        await using (var contextA = seeded.Db.CreateContext(seeded.Stranger))
        {
            var stub = new TenantScopedProbe(seeded.ProbeId, seeded.Stranger, "irrelevant");
            contextA.Probes.Remove(stub);

            var act = async () => await contextA.SaveChangesAsync(cancellationToken);

            await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        }

        await using var contextB = seeded.Db.CreateContext(seeded.Owner);
        var untouched = await contextB.Probes.SingleAsync(cancellationToken);
        untouched.Name.Should().Be("b-row");
    }
}
