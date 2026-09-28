using Decisya.Infrastructure.Persistence;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Decisya.TestInfrastructure;

/// <summary>
/// One database created by <see cref="PostgresFixture.CreateDatabaseAsync{TContext}"/> (issue
/// #22). Hands out <typeparamref name="TContext"/> instances built through
/// <c>TenantDbContext</c>'s constructor convention
/// (<c>(DbContextOptions&lt;TContext&gt; options, ICurrentTenant currentTenant)</c>); it never
/// exposes the connection string itself, only contexts (G2, "Credentials").
/// </summary>
public sealed class PostgresTestDatabase<TContext>
    where TContext : TenantDbContext
{
    private readonly string _connectionString;

    internal PostgresTestDatabase(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>A new <typeparamref name="TContext"/> instance against this database, under the given ambient tenant.</summary>
    public TContext CreateContext(ICurrentTenant currentTenant)
    {
        ArgumentNullException.ThrowIfNull(currentTenant);

        var options = new DbContextOptionsBuilder<TContext>().UseNpgsql(_connectionString).Options;

        return (TContext)Activator.CreateInstance(typeof(TContext), options, currentTenant)!;
    }

    /// <summary>A new <typeparamref name="TContext"/> instance under a <see cref="TestCurrentTenant"/> resolved to <paramref name="tenant"/> (<see cref="TenantResolution.For(TenantId)"/>).</summary>
    public TContext CreateContext(TenantId tenant) =>
        CreateContext(new TestCurrentTenant { Resolution = TenantResolution.For(tenant) });
}
