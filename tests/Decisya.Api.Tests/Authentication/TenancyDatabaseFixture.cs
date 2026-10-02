using Decisya.Modules.Tenancy.Infrastructure;
using Decisya.TestInfrastructure;
using Microsoft.EntityFrameworkCore;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// Issue #21, G2 "API tests": one throwaway, migrated Postgres database per test run, so the
/// G3 G4-21-01/G4-21-02 Integration tests exercise the real <c>TenancyDbContext</c> migrations
/// against a real Postgres 18 instance, not a schema built by <c>EnsureCreated</c>. Lazy start
/// (testcontainers skill): nothing happens until the first <see cref="EnsureStartedAsync"/>
/// call, so this stays free of Docker in the unit lane even though it is registered as an
/// assembly fixture. Shared by every test in this assembly that asks for it, so every caller
/// must use its own randomly generated tenant id and user ids (never a fixed constant) to stay
/// isolated from every other test sharing the same database.
/// </summary>
/// <remarks>
/// G2 specifies running the real <c>Decisya.Infrastructure.Migrator</c> (migrate, then
/// provision the least-privilege <c>decisya_tenancy</c> role), so the API's own tests exercise
/// the same connection the production API uses. Referencing that project from
/// <c>Decisya.Api.Tests</c> is not possible as things stand: it is a second
/// <c>OutputType=Exe</c> top-level-statements project, so its implicit <c>Program</c> type
/// collides (CS0433) with <c>Decisya.Api</c>'s own <c>Program</c> — the very type
/// <c>WebApplicationFactory&lt;Program&gt;</c> already needs throughout this project — and it
/// also pulls in <c>Microsoft.EntityFrameworkCore.Design</c> (pinned 10.0.12), which conflicts
/// (MSB3277) with the plain <c>Microsoft.EntityFrameworkCore</c> version
/// <c>Npgsql.EntityFrameworkCore.PostgreSQL</c> resolves for <c>Decisya.Api</c>'s own graph.
/// Both are reported in the G4 evidence for #21. This fixture instead runs
/// <c>TenancyDbContext.Database.MigrateAsync</c> directly — the exact same compiled migrations
/// <c>MigrationRunner</c> calls — against the fixture's owner (superuser) connection, and the
/// API under test connects with that same owner connection. It therefore does not exercise the
/// least-privilege <c>decisya_tenancy</c> role's own DML-only restriction; that is G4-21-05,
/// not this issue's G4-21-01/G4-21-02.
/// </remarks>
public sealed class TenancyDatabaseFixture : IAsyncDisposable
{
    private readonly PostgresFixture _postgres = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);

    private string? _connectionString;

    /// <summary>
    /// A connection string to a freshly migrated, empty-of-data Tenancy schema. Never log or
    /// print the returned value: it carries the container's superuser password.
    /// </summary>
    public async Task<string> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_connectionString is not null)
        {
            return _connectionString;
        }

        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connectionString is not null)
            {
                return _connectionString;
            }

            var ownerConnectionString = await CreateMigratedDatabaseAsync(cancellationToken).ConfigureAwait(false);

            _connectionString = ownerConnectionString;
            return _connectionString;
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>
    /// A second, separate, freshly migrated and empty Tenancy database on the same container, for a
    /// test that must count every row in the database (for example "no Tenant row is created")
    /// and so cannot share <see cref="EnsureStartedAsync"/>'s database with parallel tests. Never
    /// log or print the returned value.
    /// </summary>
    public async Task<string> CreateFreshDatabaseAsync(CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        return await CreateMigratedDatabaseAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Issue #25: a fresh database with the Tenancy, Entitlements and Audit schemas all migrated by their own
    /// compiled migrations (the three <c>MigrateAsync</c> calls <c>MigrationRunner</c> makes), for the admin
    /// end-to-end tests that need <c>tenancy.tenants</c>, the entitlement tables and <c>audit.audit_records</c>
    /// together. The API connects as the owner; the least-privilege roles are covered by the module and
    /// Migrator tests. Never log or print the returned value.
    /// </summary>
    public async Task<string> CreateFullyMigratedDatabaseAsync(CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var ownerConnectionString = await CreateMigratedDatabaseAsync(cancellationToken).ConfigureAwait(false);

        var entitlements = new DbContextOptionsBuilder<Decisya.Modules.Entitlements.Infrastructure.EntitlementsDbContext>();
        Decisya.Modules.Entitlements.Infrastructure.EntitlementsDbContextOptions.Configure(entitlements, ownerConnectionString);
        await using (var db = new Decisya.Modules.Entitlements.Infrastructure.EntitlementsDbContext(entitlements.Options, new TestCurrentTenant()))
        {
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }

        var audit = new DbContextOptionsBuilder<Decisya.Modules.Audit.Infrastructure.AuditDbContext>();
        Decisya.Modules.Audit.Infrastructure.AuditDbContextOptions.Configure(audit, ownerConnectionString);
        await using (var db = new Decisya.Modules.Audit.Infrastructure.AuditDbContext(audit.Options, new TestCurrentTenant()))
        {
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }

        return ownerConnectionString;
    }

    private async Task<string> CreateMigratedDatabaseAsync(CancellationToken cancellationToken)
    {
        var ownerConnectionString = await _postgres.CreateEmptyDatabaseAsync(cancellationToken).ConfigureAwait(false);

        var optionsBuilder = new DbContextOptionsBuilder<TenancyDbContext>();
        TenancyDbContextOptions.Configure(optionsBuilder, ownerConnectionString);

        // Resolution stays Invalid (TestCurrentTenant's own default): DDL runs no filtered
        // query, the same reasoning MigrationRunner's own MigratorInvalidCurrentTenant uses.
        await using (var db = new TenancyDbContext(optionsBuilder.Options, new TestCurrentTenant()))
        {
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }

        return ownerConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync().ConfigureAwait(false);
        _startLock.Dispose();
    }
}
