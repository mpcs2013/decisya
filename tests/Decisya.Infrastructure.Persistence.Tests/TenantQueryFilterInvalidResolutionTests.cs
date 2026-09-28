using System.Data.Common;
using Decisya.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Decisya.Infrastructure.Persistence.Tests;

/// <summary>
/// Issue #22, G4-22-01: with an <c>Invalid</c> ambient resolution — including a
/// <see cref="TestCurrentTenant"/> nobody set, its documented default — a collection query,
/// <c>ExecuteUpdateAsync</c>, <c>ExecuteDeleteAsync</c> and <c>SaveChangesAsync</c> all throw
/// <see cref="TenantIsolationException"/> while EF extracts parameter values, before any
/// command is created. That needs no Postgres connection at all: a
/// <see cref="DbCommandInterceptor"/> counts commands created against a connection string
/// nothing ever dials (the same "model-only" technique as
/// <c>Decisya.ArchitectureTests.TenantDbContextGuardTests</c>), and proves the count stays
/// zero. Deliberately outside <c>[Trait("Category", "Integration")]</c>, so this project is
/// never empty for the unit lane and the pre-push hook (testcontainers skill).
/// </summary>
public sealed class TenantQueryFilterInvalidResolutionTests
{
    private const string ModelOnlyConnectionString =
        "Host=model-only.invalid;Database=probe-tests;Username=probe-tests;Password=probe-tests";

    [Fact]
    public async Task With_an_Invalid_resolution_a_collection_query_sends_no_command_and_throws_InvalidTenant()
    {
        var interceptor = new CommandCountInterceptor();
        await using var context = CreateContext(interceptor);

        var caught = await Record.ExceptionAsync(async () =>
            await context.Probes.ToListAsync(TestContext.Current.CancellationToken));

        AssertInvalidTenant(caught);
        interceptor.CommandCount.Should().Be(0);
    }

    [Fact]
    public async Task With_an_Invalid_resolution_ExecuteUpdateAsync_sends_no_command_and_throws_InvalidTenant()
    {
        var interceptor = new CommandCountInterceptor();
        await using var context = CreateContext(interceptor);

        var caught = await Record.ExceptionAsync(async () =>
            await context.Probes.ExecuteUpdateAsync(
                setters => setters.SetProperty(p => p.Name, "irrelevant"),
                TestContext.Current.CancellationToken));

        AssertInvalidTenant(caught);
        interceptor.CommandCount.Should().Be(0);
    }

    [Fact]
    public async Task With_an_Invalid_resolution_ExecuteDeleteAsync_sends_no_command_and_throws_InvalidTenant()
    {
        var interceptor = new CommandCountInterceptor();
        await using var context = CreateContext(interceptor);

        var caught = await Record.ExceptionAsync(async () =>
            await context.Probes.ExecuteDeleteAsync(TestContext.Current.CancellationToken));

        AssertInvalidTenant(caught);
        interceptor.CommandCount.Should().Be(0);
    }

    [Fact]
    public async Task With_an_Invalid_resolution_SaveChangesAsync_sends_no_command_and_throws_InvalidTenant()
    {
        var interceptor = new CommandCountInterceptor();
        await using var context = CreateContext(interceptor);

        var caught = await Record.ExceptionAsync(async () =>
            await context.SaveChangesAsync(TestContext.Current.CancellationToken));

        AssertInvalidTenant(caught);
        interceptor.CommandCount.Should().Be(0);
    }

    private static ProbeDbContext CreateContext(CommandCountInterceptor interceptor)
    {
        var options = new DbContextOptionsBuilder<ProbeDbContext>()
            .UseNpgsql(ModelOnlyConnectionString)
            .AddInterceptors(interceptor)
            .Options;

        // TestCurrentTenant's default Resolution is Invalid (default(TenantResolution)) —
        // exactly the "nobody set it" case G4-22-01 asks for.
        return new ProbeDbContext(options, new TestCurrentTenant());
    }

    private static void AssertInvalidTenant(Exception? caught)
    {
        caught.Should().NotBeNull("every operation under an Invalid resolution must throw before reaching the database");

        var isolationException = caught as TenantIsolationException ?? caught!.InnerException as TenantIsolationException;

        isolationException.Should().NotBeNull("the exception (or its inner exception) must be a TenantIsolationException");
        isolationException!.Violation.Should().Be(TenantIsolationViolation.InvalidTenant);
    }

    private sealed class CommandCountInterceptor : DbCommandInterceptor
    {
        public int CommandCount { get; private set; }

        public override InterceptionResult<DbCommand> CommandCreating(CommandCorrelatedEventData eventData, InterceptionResult<DbCommand> result)
        {
            CommandCount++;
            return base.CommandCreating(eventData, result);
        }
    }
}
