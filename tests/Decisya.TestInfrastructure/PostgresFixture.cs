using System.Security.Cryptography;
using Decisya.AppHost;
using Decisya.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Decisya.TestInfrastructure;

/// <summary>
/// One throwaway Postgres container, shared by every test in the assembly that needs one
/// (issue #22, testcontainers skill). The constructor does nothing: the container starts,
/// guarded by a semaphore and a bounded two-minute timeout, on the first
/// <see cref="CreateDatabaseAsync{TContext}"/> call, so an
/// <c>[assembly: AssemblyFixture(typeof(PostgresFixture))]</c> stays free of Docker in the unit
/// lane (xUnit v3 initialises assembly fixtures before trait filtering runs). Every call after
/// the first creates a fresh, empty database inside the same container — a database per call,
/// not a schema per test, because module contexts hard-code their own schema — so tests never
/// share rows. Test databases are removed with the container on <see cref="DisposeAsync"/>.
/// </summary>
public sealed class PostgresFixture : IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _startLock = new(1, 1);

    private PostgreSqlContainer? _container;
    private bool _started;

    private PostgreSqlContainer Container =>
        _container ?? throw new InvalidOperationException(
            "PostgresFixture has not started yet. Call CreateDatabaseAsync first.");

    /// <summary>
    /// Starts the shared container on first call, then creates a fresh database named
    /// <c>t_</c> plus 32 random hex characters (fixture-generated, never caller input) and
    /// runs <c>Database.EnsureCreatedAsync</c> through a <typeparamref name="TContext"/> whose
    /// ambient resolution is <c>TenantResolution.Invalid</c> — schema DDL runs no filtered
    /// query, so an <c>Invalid</c> resolution is safe here. The returned
    /// <see cref="PostgresTestDatabase{TContext}"/> hands out contexts only; it never exposes
    /// the connection string.
    /// </summary>
    public async Task<PostgresTestDatabase<TContext>> CreateDatabaseAsync<TContext>(
        CancellationToken cancellationToken = default)
        where TContext : TenantDbContext
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        var databaseName = $"t_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}";
        var adminConnectionString = Container.GetConnectionString();

        // Decisya.TestInfrastructure sits outside Decisya.ArchitectureTests' scope (G3,
        // "PostgresFixture" note), so CrossTenantQueryRule's raw-SQL bypass list does not apply
        // here. A plain, unmapped DbContext (not a TenantDbContext) issues the DDL: there is no
        // tenant, and no ITenantScoped entity, involved in creating a database.
        var adminOptions = new DbContextOptionsBuilder().UseNpgsql(adminConnectionString).Options;
        await using (var admin = new DbContext(adminOptions))
        {
            // Postgres cannot parameterize an identifier in a DDL statement, so this cannot use
            // ExecuteSqlAsync's interpolated-parameter form. Safe: databaseName is generated
            // above from RandomNumberGenerator bytes through Convert.ToHexStringLower, so it is
            // always "t_" plus 32 lowercase hex characters — never caller input.
#pragma warning disable EF1002
            await admin.Database
                .ExecuteSqlRawAsync($"CREATE DATABASE \"{databaseName}\"", cancellationToken)
                .ConfigureAwait(false);
#pragma warning restore EF1002
        }

        var database = new PostgresTestDatabase<TContext>($"{adminConnectionString};Database={databaseName}");

        await using (var context = database.CreateContext(new TestCurrentTenant()))
        {
            await context.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        }

        return database;
    }

    /// <summary>Starts the container on the first call; every later call is a no-op (the same lazy-start shape as <c>KeycloakRealmFixture.EnsureStartedAsync</c>).</summary>
    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return;
        }

        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started)
            {
                return;
            }

            var image = ContainerImages.Reference(
                ContainerImages.PostgresRegistry, ContainerImages.PostgresImage,
                ContainerImages.PostgresTag, ContainerImages.PostgresSha256);

            // Per-run random password, generated in-process, in CI and on the host alike (G2,
            // T-18): no consumer outside this process needs it, so it is never printed and
            // never leaves the fixture as anything but part of a context's connection.
            var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

            _container = new PostgreSqlBuilder(image)
                .WithPassword(password)
                .Build();

            using var timeoutCts = new CancellationTokenSource(StartTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                await _container.StartAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException($"PostgresFixture: Postgres did not become ready within {StartTimeout}.");
            }

            _started = true;
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>Disposes the container — which removes every database created through it — and the start guard.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }

        _startLock.Dispose();
    }
}
