using Decisya.Api.Authentication;
using Decisya.Infrastructure.Persistence;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Decisya.Api.Tests.Messaging;

public sealed class UseTimeRow : ITenantScoped
{
    private UseTimeRow()
    {
        // EF Core materialization constructor.
    }

    public UseTimeRow(TenantId tenantId, string name)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        Name = name;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; }

    public string Name { get; private set; } = string.Empty;
}

/// <summary>A real <see cref="TenantDbContext"/> subclass, as a module's context is.</summary>
public sealed class UseTimeDbContext(DbContextOptions<UseTimeDbContext> options, ICurrentTenant currentTenant)
    : TenantDbContext(options, currentTenant)
{
    protected override void OnTenantModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<UseTimeRow>();
}

/// <summary>
/// Issue #231, G3 G4-231-08 proof (b): WolverineFx 6.39.1 builds a handler's scoped dependencies, a
/// module's <c>TenantDbContext</c> among them, before the envelope tenant middleware sets the
/// tenant. That is safe only because the context keeps the <see cref="ICurrentTenant"/> reference and
/// reads the tenant when it is used. Nothing here opens a connection: the connection string is never
/// dialled, every case throws, or compiles a query, before a command is sent (the technique of
/// <c>TenantDbContextGuardTests</c> in <c>Decisya.ArchitectureTests</c>). No Postgres fixture is used.
/// </summary>
[Trait("Category", "Unit")]
public class TenantDbContextUseTimeTests
{
    private const string ModelOnlyConnectionString =
        "Host=model-only.invalid;Database=use-time-tests;Username=use-time-tests;Password=use-time-tests";

    private static readonly TenantId TenantA = TenantId.Parse("018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f70");
    private static readonly TenantId TenantB = TenantId.Parse("018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f71");

    [Fact]
    public void A_query_under_Invalid_throws_InvalidTenant_before_any_SQL()
    {
        using var context = CreateContext(new RequestCaller());

        var query = () => context.Set<UseTimeRow>().ToQueryString();

        AssertViolation(query.Should().Throw<Exception>().Which, TenantIsolationViolation.InvalidTenant);
    }

    [Fact]
    public async Task A_save_under_Invalid_throws_InvalidTenant_even_with_nothing_to_save()
    {
        using var context = CreateContext(new RequestCaller());

        var act = async () => await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<TenantIsolationException>()).Which.Violation.Should().Be(TenantIsolationViolation.InvalidTenant);
    }

    [Fact]
    public void A_context_built_under_Invalid_queries_as_tenant_A_once_the_same_ICurrentTenant_instance_is_set()
    {
        var caller = new RequestCaller();
        using var context = CreateContext(caller);
        var underInvalid = () => context.Set<UseTimeRow>().ToQueryString();
        AssertViolation(underInvalid.Should().Throw<Exception>().Which, TenantIsolationViolation.InvalidTenant);

        // What the middleware does, to the instance the context already holds.
        caller.Set(TenantResolution.For(TenantA), "user-a", isPlatformAdmin: false);

        var sql = context.Set<UseTimeRow>().ToQueryString();

        sql.Should().Contain(TenantA.ToString(), "the filter now carries tenant A, read at query time");
        sql.Should().NotContain(TenantB.ToString());
    }

    [Fact]
    public async Task A_context_built_under_Invalid_saves_as_tenant_A_once_the_same_ICurrentTenant_instance_is_set()
    {
        var caller = new RequestCaller();
        using var context = CreateContext(caller);
        caller.Set(TenantResolution.For(TenantA), "user-a", isPlatformAdmin: false);
        context.Add(new UseTimeRow(TenantB, "belongs to B"));

        var act = async () => await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // A frozen Invalid would say InvalidTenant. Reaching the per-entity check shows the save read tenant A.
        (await act.Should().ThrowAsync<TenantIsolationException>()).Which.Violation.Should().Be(TenantIsolationViolation.TenantMismatch);
    }

    private static void AssertViolation(Exception exception, TenantIsolationViolation violation)
    {
        var isolation = exception as TenantIsolationException ?? exception.InnerException as TenantIsolationException;

        isolation.Should().NotBeNull("the failure must be the tenant guard, not a connection error");
        isolation!.Violation.Should().Be(violation);
    }

    private static UseTimeDbContext CreateContext(ICurrentTenant currentTenant)
    {
        var options = new DbContextOptionsBuilder<UseTimeDbContext>()
            .UseNpgsql(ModelOnlyConnectionString)
            .Options;

        return new UseTimeDbContext(options, currentTenant);
    }
}
