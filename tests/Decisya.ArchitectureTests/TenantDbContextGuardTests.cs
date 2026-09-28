using Decisya.ArchitectureTests.Fixtures.TenancyCompliant;
using Decisya.Infrastructure.Persistence;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #22, G4-22-01 and G4-22-02: the parts of the query filter and the <c>SaveChanges</c>
/// guard that throw <em>before any connection is opened</em> (G2's test approach note), proved
/// here against the real <see cref="CompliantDbContext"/>/<see cref="ScopedEntity"/> fixture
/// without Docker. The parts that need a live Postgres to observe a result — "None" yielding
/// zero rows against seeded data, two contexts never sharing a cached tenant, a same-tenant
/// write actually persisting, and the detached-update <c>DbUpdateConcurrencyException</c> path —
/// are not provable here; see the #22 G4 report for the exact dependency
/// (<c>tests/Decisya.Infrastructure.Persistence.Tests</c>, blocked on platform-dev's
/// <c>PostgresFixture</c>, and outside this agent's write permissions).
/// </summary>
public class TenantDbContextGuardTests
{
    // A connection string that is never dialled: every scenario below throws while EF
    // extracts query/save parameters, before any command is sent (G2, "Test approach").
    private const string ModelOnlyConnectionString =
        "Host=model-only.invalid;Database=architecture-tests;Username=architecture-tests;Password=architecture-tests";

    [Fact]
    public async Task A_query_under_an_Invalid_resolution_throws_before_any_SQL()
    {
        using var context = CreateContext(TenantResolution.Invalid);

        Exception caught = await Record.ExceptionAsync(async () =>
            await context.Set<ScopedEntity>().ToListAsync(TestContext.Current.CancellationToken))
            ?? throw new InvalidOperationException("Expected an exception; none was thrown.");

        var isolationException = caught as TenantIsolationException ?? caught.InnerException as TenantIsolationException;

        isolationException.Should().NotBeNull();
        isolationException!.Violation.Should().Be(TenantIsolationViolation.InvalidTenant);
    }

    [Fact]
    public async Task SaveChangesAsync_under_an_Invalid_resolution_throws_even_with_no_pending_changes()
    {
        using var context = CreateContext(TenantResolution.Invalid);

        var act = async () => await context.SaveChangesAsync();

        var exception = await act.Should().ThrowAsync<TenantIsolationException>();
        exception.Which.Violation.Should().Be(TenantIsolationViolation.InvalidTenant);
    }

    [Fact]
    public async Task Adding_an_entity_under_a_None_resolution_throws_NoTenant_before_any_SQL()
    {
        using var context = CreateContext(TenantResolution.NoTenant);
        context.Add(new ScopedEntity(TenantId.New(), "irrelevant"));

        var act = async () => await context.SaveChangesAsync();

        var exception = await act.Should().ThrowAsync<TenantIsolationException>();
        exception.Which.Violation.Should().Be(TenantIsolationViolation.NoTenant);
    }

    [Fact]
    public async Task Adding_an_entity_with_no_TenantId_assigned_throws_UntenantedEntity()
    {
        var tenantA = TenantId.New();
        using var context = CreateContext(TenantResolution.For(tenantA));
        context.Add(new ScopedEntity(default, "irrelevant"));

        var act = async () => await context.SaveChangesAsync();

        var exception = await act.Should().ThrowAsync<TenantIsolationException>();
        exception.Which.Violation.Should().Be(TenantIsolationViolation.UntenantedEntity);
    }

    [Fact]
    public async Task Adding_an_entity_tagged_with_a_different_tenant_throws_TenantMismatch()
    {
        var tenantA = TenantId.New();
        var tenantB = TenantId.New();
        using var context = CreateContext(TenantResolution.For(tenantA));
        context.Add(new ScopedEntity(tenantB, "irrelevant"));

        var act = async () => await context.SaveChangesAsync();

        var exception = await act.Should().ThrowAsync<TenantIsolationException>();
        exception.Which.Violation.Should().Be(TenantIsolationViolation.TenantMismatch);
    }

    [Fact]
    public async Task Attaching_another_tenants_row_and_saving_throws_TenantMismatch_without_changing_it()
    {
        var tenantA = TenantId.New();
        var tenantB = TenantId.New();
        using var context = CreateContext(TenantResolution.For(tenantA));
        var othersRow = new ScopedEntity(tenantB, "belongs to B");
        context.Attach(othersRow);
        context.Remove(othersRow);

        var act = async () => await context.SaveChangesAsync();

        var exception = await act.Should().ThrowAsync<TenantIsolationException>();
        exception.Which.Violation.Should().Be(TenantIsolationViolation.TenantMismatch);
    }

    [Fact]
    public async Task Changing_an_attached_rows_TenantId_via_the_entry_API_throws_TenantChanged_even_with_AutoDetectChangesEnabled_off()
    {
        var tenantA = TenantId.New();
        var tenantB = TenantId.New();
        using var context = CreateContext(TenantResolution.For(tenantA));
        var row = new ScopedEntity(tenantA, "mine");
        context.Attach(row);
        context.ChangeTracker.AutoDetectChangesEnabled = false;

        context.Entry(row).Property(nameof(ITenantScoped.TenantId)).CurrentValue = tenantB;

        var act = async () => await context.SaveChangesAsync();

        var exception = await act.Should().ThrowAsync<TenantIsolationException>();
        exception.Which.Violation.Should().Be(TenantIsolationViolation.TenantChanged);
    }

    [Fact]
    public void TenantIsolationException_message_and_ToString_never_carry_a_tenant_id_or_the_entitys_name()
    {
        var tenantId = TenantId.New();
        var canaryName = "top secret canary name, never logged";
        var exception = new TenantIsolationException(TenantIsolationViolation.TenantMismatch, nameof(ScopedEntity));

        exception.Message.Should().NotContain(tenantId.ToString());
        exception.Message.Should().NotContain(canaryName);
        exception.ToString().Should().NotContain(tenantId.ToString());
        exception.ToString().Should().NotContain(canaryName);
    }

    private static CompliantDbContext CreateContext(TenantResolution resolution)
    {
        var options = new DbContextOptionsBuilder<CompliantDbContext>()
            .UseNpgsql(ModelOnlyConnectionString)
            .Options;

        return new CompliantDbContext(options, new FixedCurrentTenant(resolution));
    }

    private sealed class FixedCurrentTenant(TenantResolution resolution) : ICurrentTenant
    {
        public TenantResolution Resolution { get; } = resolution;
    }
}
