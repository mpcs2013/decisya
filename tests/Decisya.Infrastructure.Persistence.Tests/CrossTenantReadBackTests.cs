using Decisya.TestInfrastructure;

namespace Decisya.Infrastructure.Persistence.Tests;

/// <summary>
/// Issue #22, G4-22-03 (Marco, 2026-09-28): these are hazard-pinning tests, not isolation
/// tests. They confirm, against a real Postgres 18 container, what G3 could not confirm from
/// the EF Core binary alone (T-07): <c>EntityEntry.GetDatabaseValuesAsync()</c> and
/// <c>EntityEntry.ReloadAsync()</c> both look a row up by primary key alone, with the tenant
/// query filter ignored, by design (EF's <c>EntityFinder</c>). Attaching a stub that carries
/// tenant B's real row id while running as tenant A and calling either member returns tenant
/// B's real row, not "not found".
/// <para>
/// This is safe in Decisya only because <c>CrossTenantQueryRule</c> (ADR-0001,
/// <c>tests/Decisya.ArchitectureTests</c>) bans both members — <c>GetDatabaseValues(Async)</c>
/// and <c>Reload(Async)</c> — everywhere outside a type carrying <c>[AllowCrossTenant]</c>, the
/// same as it bans <c>IgnoreQueryFilters</c>. The architecture rule is the control; these tests
/// exist only to pin the underlying EF behaviour that rule depends on (#21's boundary B-3 also
/// requires conflict handlers to never surface database values to a caller).
/// </para>
/// <para>
/// <b>If either test here ever fails</b> (the leaked value stops coming back), that means EF
/// changed this behaviour, not that the hazard was fixed by this test suite — revisit whether
/// <c>CrossTenantQueryRule</c>'s ban is still needed, do not simply flip the assertion.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class CrossTenantReadBackTests
{
    private readonly PostgresFixture _pg;

    public CrossTenantReadBackTests(PostgresFixture pg)
    {
        _pg = pg;
    }

    [Fact]
    public async Task EF_GetDatabaseValuesAsync_bypasses_the_tenant_filter_so_it_is_banned_outside_AllowCrossTenant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string tenantBsRealName = "tenant-B-secret-name-get-database-values";
        var seeded = await ProbeSeeding.SeedOneTenantAsync(_pg, tenantBsRealName, cancellationToken);

        await using var contextA = seeded.Db.CreateContext(seeded.Stranger);
        var stub = new TenantScopedProbe(seeded.ProbeId, seeded.Stranger, "attacker-supplied");
        var entry = contextA.Attach(stub);

        var databaseValues = await entry.GetDatabaseValuesAsync(cancellationToken);

        // Observed EF behaviour (G4-22-03/T-07): GetDatabaseValuesAsync looks the row up by
        // primary key alone, ignoring the tenant filter, so it finds tenant B's row even
        // though contextA's ambient tenant is a stranger to it.
        databaseValues.Should().NotBeNull(
            "GetDatabaseValuesAsync looks a row up by id alone, ignoring the tenant filter " +
            "(G4-22-03/T-07); this is only safe because CrossTenantQueryRule (ADR-0001) bans " +
            "this member outside an [AllowCrossTenant] type — if this assertion ever fails, EF " +
            "changed its behaviour, so revisit whether that ban is still needed, don't just " +
            "flip this assertion");

        // The one assertion that proves the hazard, not merely that something came back:
        // tenant A gets tenant B's actual secret value.
        databaseValues!.GetValue<string>(nameof(TenantScopedProbe.Name)).Should().Be(
            tenantBsRealName,
            "the value GetDatabaseValuesAsync returns to tenant A is tenant B's real, secret " +
            "row content — exactly why CrossTenantQueryRule bans this member outside " +
            "[AllowCrossTenant] (G4-22-03, ADR-0001). If this assertion ever fails, EF changed " +
            "its behaviour, so revisit whether that ban is still needed, don't just flip this " +
            "assertion");
    }

    [Fact]
    public async Task EF_ReloadAsync_bypasses_the_tenant_filter_so_it_is_banned_outside_AllowCrossTenant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string tenantBsRealName = "tenant-B-secret-name-reload";
        var seeded = await ProbeSeeding.SeedOneTenantAsync(_pg, tenantBsRealName, cancellationToken);

        await using var contextA = seeded.Db.CreateContext(seeded.Stranger);
        var stub = new TenantScopedProbe(seeded.ProbeId, seeded.Stranger, "attacker-supplied");
        var entry = contextA.Attach(stub);

        await entry.ReloadAsync(cancellationToken);

        // Observed EF behaviour (G4-22-03/T-07): ReloadAsync also looks the row up by primary
        // key alone, ignoring the tenant filter, and overwrites the stub's current values with
        // tenant B's real row content. This is the one assertion that proves the hazard, not
        // merely that Reload changed something.
        stub.Name.Should().Be(
            tenantBsRealName,
            "ReloadAsync overwrote the stub with tenant B's real, secret row content, ignoring " +
            "the tenant filter (G4-22-03/T-07); this is only safe because CrossTenantQueryRule " +
            "(ADR-0001) bans this member outside an [AllowCrossTenant] type — if this assertion " +
            "ever fails, EF changed its behaviour, so revisit whether that ban is still needed, " +
            "don't just flip this assertion");
    }
}
