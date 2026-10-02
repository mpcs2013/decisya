using System.Data.Common;
using Decisya.Modules.Tenancy.Application;
using Decisya.Modules.Tenancy.Domain;
using Decisya.Modules.Tenancy.Infrastructure;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;

namespace Decisya.Modules.Tenancy.Tests.Application;

/// <summary>
/// Issue #25 (G2 D4, G3 T-09; ADR-0012 amendment 1 point 6): <c>TenantExistence</c> against a real
/// Postgres 18 database. It answers "does the tenant this scope names exist" through the ordinary
/// tenant filter, throws for <c>None</c> and <c>Invalid</c> before any SQL, and never sees another
/// tenant's row (isolation-test skill).
/// </summary>
[Trait("Category", "Integration")]
public sealed class TenantExistenceTests(PostgresFixture pg)
{
    private static readonly Instant Now = SystemClock.Instance.GetCurrentInstant();

    private async Task<(TenantExistence Sut, CommandCounter Commands, DbContextOptions<TenancyDbContext> Options)> CreateAsync(CancellationToken ct)
    {
        var connectionString = await pg.CreateEmptyDatabaseAsync(ct);
        var commands = new CommandCounter();
        var builder = new DbContextOptionsBuilder<TenancyDbContext>();
        TenancyDbContextOptions.Configure(builder, connectionString);

        await using (var schema = new TenancyDbContext(builder.Options, new TestCurrentTenant()))
        {
            await schema.Database.EnsureCreatedAsync(ct);
        }

        builder.AddInterceptors(commands);
        return (new TenantExistence(builder.Options), commands, builder.Options);
    }

    private static async Task SeedAsync(DbContextOptions<TenancyDbContext> options, TenantId tenant, CancellationToken ct)
    {
        await using var db = new TenancyDbContext(options, new TestCurrentTenant { Resolution = TenantResolution.For(tenant) });
        db.Tenants.Add(new Tenant(tenant, Now));
        await db.SaveChangesAsync(ct);
    }

    [Fact]
    public async Task An_existing_tenant_gives_true()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = TenantId.New();
        var (sut, _, options) = await CreateAsync(ct);
        await SeedAsync(options, a, ct);

        (await sut.ExistsAsync(TenantResolution.For(a), ct)).Should().BeTrue();
    }

    [Fact]
    public async Task A_missing_tenant_gives_false()
    {
        var ct = TestContext.Current.CancellationToken;
        var (sut, _, options) = await CreateAsync(ct);
        await SeedAsync(options, TenantId.New(), ct);

        (await sut.ExistsAsync(TenantResolution.For(TenantId.New()), ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Under_the_scope_of_tenant_A_tenant_Bs_row_is_never_seen()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = TenantId.New();
        var b = TenantId.New();
        var (sut, commands, options) = await CreateAsync(ct);
        await SeedAsync(options, b, ct);
        commands.Reset();

        // Only B exists: asking under A's scope is false, under B's scope true.
        (await sut.ExistsAsync(TenantResolution.For(a), ct)).Should().BeFalse();
        (await sut.ExistsAsync(TenantResolution.For(b), ct)).Should().BeTrue();

        commands.Count.Should().Be(2, "one existence query per call, nothing else");
        commands.Texts.Should().OnlyContain(t => t.Contains("tenants", StringComparison.OrdinalIgnoreCase));
        commands.Texts.Should().OnlyContain(t => t.Contains("tenant_id", StringComparison.OrdinalIgnoreCase), "the query carries the tenant filter");

        // Both exist: each scope still answers for its own tenant only, and a third scope for neither.
        await SeedAsync(options, a, ct);
        (await sut.ExistsAsync(TenantResolution.For(a), ct)).Should().BeTrue();
        (await sut.ExistsAsync(TenantResolution.For(TenantId.New()), ct)).Should().BeFalse();
    }

    [Fact]
    public async Task A_scope_of_kind_None_throws_before_any_SQL()
    {
        var ct = TestContext.Current.CancellationToken;
        var (sut, commands, options) = await CreateAsync(ct);
        await SeedAsync(options, TenantId.New(), ct);
        commands.Reset();

        var act = () => sut.ExistsAsync(TenantResolution.NoTenant, ct);

        await act.Should().ThrowAsync<ArgumentException>();
        commands.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_scope_of_kind_Invalid_throws_before_any_SQL()
    {
        var ct = TestContext.Current.CancellationToken;
        var (sut, commands, options) = await CreateAsync(ct);
        await SeedAsync(options, TenantId.New(), ct);
        commands.Reset();

        var act = () => sut.ExistsAsync(TenantResolution.Invalid, ct);

        await act.Should().ThrowAsync<ArgumentException>();
        commands.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_database_failure_propagates_and_is_never_turned_into_an_answer()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = new DbContextOptionsBuilder<TenancyDbContext>();
        TenancyDbContextOptions.Configure(builder, "Host=db.invalid;Database=decisya;Username=x;Password=x;Timeout=1;Command Timeout=1");
        var sut = new TenantExistence(builder.Options);

        var act = () => sut.ExistsAsync(TenantResolution.For(TenantId.New()), ct);

        await act.Should().ThrowAsync<Exception>();
    }

    /// <summary>Counts and records every command the existence check sends.</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        private readonly List<string> _texts = [];

        public int Count
        {
            get
            {
                lock (_texts)
                {
                    return _texts.Count;
                }
            }
        }

        public IReadOnlyList<string> Texts
        {
            get
            {
                lock (_texts)
                {
                    return [.. _texts];
                }
            }
        }

        public void Reset()
        {
            lock (_texts)
            {
                _texts.Clear();
            }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Record(DbCommand command)
        {
            lock (_texts)
            {
                _texts.Add(command.CommandText);
            }
        }
    }
}
