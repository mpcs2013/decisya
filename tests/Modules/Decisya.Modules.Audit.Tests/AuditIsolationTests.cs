using Decisya.Infrastructure.Persistence;
using Decisya.Modules.Audit.Contracts;
using Decisya.Modules.Audit.Domain;
using Decisya.Modules.Audit.Infrastructure;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Decisya.Modules.Audit.Tests;

/// <summary>
/// G1 Story 5 (the isolation-test skill): tenant A can neither read nor change tenant B's
/// <see cref="AuditRecord"/>, by collection or by id, and a write for B under A is refused by the #22
/// tenant guard. Real Postgres 18, through <c>AuditDbContext</c> built by the fixture (the test seam of
/// G2's reconciliation table). Test names are the Gherkin scenario titles.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuditIsolationTests(PostgresFixture pg)
{
    private static readonly TenantId TenantA = TenantId.From(Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7"));
    private static readonly TenantId TenantB = TenantId.From(Guid.Parse("2f1a8d5e-3b4c-4e6f-9a7b-1c2d3e4f5a6b"));
    private static readonly Instant At = Instant.FromUtc(2026, 10, 1, 9, 0);

    private static AuditRecord Record(TenantId tenant, AuditAction action = AuditAction.EntitlementsOverrideGrant) =>
        AuditRecord.Create(
            tenant,
            action,
            action == AuditAction.EntitlementsTrialStart ? null : "forecasting.scenarios",
            "3f2d1c9a-8b7e-4d6c-a5f4-0e1d2c3b4a59",
            At,
            "4bf92f3577b34da6a3ce929d0e0e4736");

    private static async Task<(Guid A, Guid B)> SeedAsync(PostgresTestDatabase<AuditDbContext> db, CancellationToken ct)
    {
        var a = Record(TenantA);
        var b = Record(TenantB, AuditAction.EntitlementsTrialStart);

        await using (var asA = db.CreateContext(TenantA))
        {
            asA.Records.Add(a);
            await asA.SaveChangesAsync(ct);
        }

        await using (var asB = db.CreateContext(TenantB))
        {
            asB.Records.Add(b);
            await asB.SaveChangesAsync(ct);
        }

        return (a.Id, b.Id);
    }

    [Fact]
    public async Task Tenant_A_cannot_read_tenant_Bs_audit_records()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = await pg.CreateDatabaseAsync<AuditDbContext>(ct);
        var (aId, bId) = await SeedAsync(db, ct);

        await using var asA = db.CreateContext(TenantA);

        // By a collection query: only A's own record.
        (await asA.Records.Select(r => r.Id).ToListAsync(ct)).Should().Equal(aId);

        // By tenant B's own id.
        (await asA.Records.SingleOrDefaultAsync(r => r.Id == bId, ct)).Should().BeNull();
        (await asA.Records.CountAsync(r => r.TenantId == TenantB, ct)).Should().Be(0);
    }

    [Fact]
    public async Task Tenant_A_cannot_update_or_delete_tenant_Bs_audit_records_by_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = await pg.CreateDatabaseAsync<AuditDbContext>(ct);
        var (_, bId) = await SeedAsync(db, ct);

        await using (var asA = db.CreateContext(TenantA))
        {
            (await asA.Records.Where(r => r.Id == bId).ExecuteUpdateAsync(s => s.SetProperty(r => r.TraceId, "tampered"), ct)).Should().Be(0);
            (await asA.Records.Where(r => r.Id == bId).ExecuteDeleteAsync(ct)).Should().Be(0);
        }

        await using var asB = db.CreateContext(TenantB);
        var survivor = (await asB.Records.AsNoTracking().ToListAsync(ct)).Should().ContainSingle().Which;
        survivor.Id.Should().Be(bId);
        survivor.TraceId.Should().Be("4bf92f3577b34da6a3ce929d0e0e4736");
    }

    [Fact]
    public async Task Tenant_A_cannot_write_a_record_that_belongs_to_tenant_B()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = await pg.CreateDatabaseAsync<AuditDbContext>(ct);

        await using (var asA = db.CreateContext(TenantA))
        {
            asA.Records.Add(Record(TenantB));
            var act = () => asA.SaveChangesAsync(ct);

            var failure = await act.Should().ThrowAsync<TenantIsolationException>();
            failure.Which.Violation.Should().Be(TenantIsolationViolation.TenantMismatch);
        }

        await using var asB = db.CreateContext(TenantB);
        (await asB.Records.CountAsync(ct)).Should().Be(0, "the refused save wrote no row");
    }

    [Fact]
    public async Task A_no_tenant_context_reads_zero_audit_records()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = await pg.CreateDatabaseAsync<AuditDbContext>(ct);
        await SeedAsync(db, ct);

        await using var none = db.CreateContext(new TestCurrentTenant { Resolution = TenantResolution.NoTenant });

        (await none.Records.ToListAsync(ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_invalid_context_throws_before_a_query_reaches_the_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new DbContextOptionsBuilder<AuditDbContext>();
        AuditDbContextOptions.Configure(options, "Host=db.invalid;Database=decisya;Username=placeholder;Password=placeholder;Timeout=1;Command Timeout=1");
        await using var invalid = new AuditDbContext(options.Options, new TestCurrentTenant { Resolution = TenantResolution.Invalid });

        // An unreachable host: any attempt to run SQL would fail with a connection error, not the isolation exception.
        var act = () => invalid.Records.ToListAsync(ct);

        var failure = await act.Should().ThrowAsync<TenantIsolationException>();
        failure.Which.Violation.Should().Be(TenantIsolationViolation.InvalidTenant);
    }

    [Fact]
    public async Task The_module_persists_only_through_ITenantScoped_aggregates()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = await pg.CreateDatabaseAsync<AuditDbContext>(ct);
        await using var context = db.CreateContext(TenantA);

        var entityTypes = context.Model.GetEntityTypes().ToList();

        entityTypes.Should().ContainSingle();
        entityTypes.Should().OnlyContain(e => typeof(ITenantScoped).IsAssignableFrom(e.ClrType));
        entityTypes.Select(e => e.GetSchema()).Should().OnlyContain(s => s == "audit");
        entityTypes.Select(e => e.GetTableName()).Should().Equal("audit_records");
        context.Model.GetDefaultSchema().Should().Be("audit");
    }
}
