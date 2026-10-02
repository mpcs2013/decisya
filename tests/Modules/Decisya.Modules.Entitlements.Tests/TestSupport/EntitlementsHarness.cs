using System.Security.Cryptography;
using Decisya.Infrastructure.Migrator;
using Decisya.Modules.Audit;
using Decisya.Modules.Audit.Infrastructure;
using Decisya.Modules.Entitlements.Application;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Entitlements.Domain;
using Decisya.Modules.Entitlements.Infrastructure;
using Decisya.SharedKernel.Results;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Testing;
using Npgsql;

namespace Decisya.Modules.Entitlements.Tests.TestSupport;

/// <summary>
/// The real module, wired the way the API wires it (<c>AddAuditModule</c>, then
/// <c>AddEntitlementsModule</c>), over either a real Postgres 18 database or a placeholder host
/// that throws on any connection attempt (G3: "No database access is proved with a placeholder
/// <c>Host=db.invalid</c>"). Every call runs in its own DI scope with its own
/// <see cref="TestCurrentTenant"/> and <see cref="TestCurrentCaller"/>, so each one is what a
/// separate request would be. Debug logging is captured, EF Core and Npgsql included.
/// </summary>
internal sealed class EntitlementsHarness : IAsyncDisposable
{
    internal const string UnreachableConnectionString =
        "Host=db.invalid;Database=decisya;Username=placeholder;Password=placeholder;Timeout=1;Command Timeout=1";

    /// <summary>The fixed start instant the G1 scenarios use.</summary>
    internal static readonly Instant Start = Instant.FromUtc(2026, 10, 1, 9, 0);

    internal static readonly TenantId TenantA = TenantId.From(Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7"));
    internal static readonly TenantId TenantB = TenantId.From(Guid.Parse("2f1a8d5e-3b4c-4e6f-9a7b-1c2d3e4f5a6b"));
    internal static readonly TenantId TenantC = TenantId.From(Guid.Parse("b3f0c2a4-5d6e-4f70-8a91-0b1c2d3e4f50"));

    // Postgres roles are cluster-wide: every harness that runs the migrator in this assembly's one
    // container serialises on this gate (CREATE ROLE / ALTER ROLE race otherwise), and all of them
    // use the same two role passwords, so a re-run never changes what an open login accepts.
    private static readonly SemaphoreSlim MigratorGate = new(1, 1);
    private static readonly string TenancyRolePassword = NewPassword();
    private static readonly string EntitlementsRolePassword = NewPassword();

    private readonly ServiceProvider _provider;
    private readonly string _ownerConnectionString;
    private readonly string _serviceConnectionString;

    private EntitlementsHarness(string ownerConnectionString, string serviceConnectionString, Action<IServiceCollection>? configure)
    {
        _ownerConnectionString = ownerConnectionString;
        _serviceConnectionString = serviceConnectionString;
        Clock = new FakeClock(Start);
        Logs = new CapturingLoggerProvider();

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(Logs);
        });
        services.AddSingleton<IClock>(Clock);
        services.AddScoped<TestCurrentTenant>();
        services.AddScoped<ICurrentTenant>(sp => sp.GetRequiredService<TestCurrentTenant>());
        services.AddScoped(_ => new TestCurrentCaller { Id = CallerUserId });
        services.AddScoped<ICurrentCaller>(sp => sp.GetRequiredService<TestCurrentCaller>());
        services.AddAuditModule();
        services.AddEntitlementsModule(serviceConnectionString);
        configure?.Invoke(services);
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public FakeClock Clock { get; }

    public CapturingLoggerProvider Logs { get; }

    /// <summary>
    /// The user id every scope created from now on carries as <see cref="ICurrentCaller.UserId"/>;
    /// <see langword="null"/> means "no validated caller" (the stub then throws, as the real one does).
    /// </summary>
    public string? CallerUserId { get; set; } = TestCurrentCaller.DefaultUserId;

    private static string NewPassword() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));

    /// <summary>
    /// A fresh database with both models created (<c>EnsureCreated</c> for Entitlements, so the unique
    /// indexes and check constraints are there, and the Audit migration for <c>audit.audit_records</c>).
    /// The handlers connect as the database owner here; use <see cref="CreateAsRoleAsync"/> for the real role.
    /// </summary>
    public static async Task<EntitlementsHarness> CreateAsync(
        PostgresFixture pg, CancellationToken cancellationToken, Action<IServiceCollection>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(pg);

        var connectionString = await pg.CreateEmptyDatabaseAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<EntitlementsDbContext>();
        EntitlementsDbContextOptions.Configure(options, connectionString);
        await using (var db = new EntitlementsDbContext(options.Options, new TestCurrentTenant()))
        {
            await db.Database.EnsureCreatedAsync(cancellationToken);
        }

        // EnsureCreated on a second context in a database that already has tables does nothing
        // (G2 "Harness"), so the audit table comes from the real Audit migration.
        var auditOptions = new DbContextOptionsBuilder<AuditDbContext>();
        AuditDbContextOptions.Configure(auditOptions, connectionString);
        await using (var audit = new AuditDbContext(auditOptions.Options, new TestCurrentTenant()))
        {
            await audit.Database.MigrateAsync(cancellationToken);
        }

        return new EntitlementsHarness(connectionString, connectionString, configure);
    }

    /// <summary>
    /// A fresh database migrated by the real <c>MigrationRunner.RunAsync</c>; the handlers connect as
    /// <c>decisya_entitlements</c> (INSERT-only on the audit table), while direct reads and seeding use
    /// the owner. This is the only proof that EF's insert needs no SELECT and that the savepoint paths
    /// work under the real role (G2, G4-24-02).
    /// </summary>
    public static async Task<EntitlementsHarness> CreateAsRoleAsync(
        PostgresFixture pg, CancellationToken cancellationToken, Action<IServiceCollection>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(pg);

        var owner = await pg.CreateEmptyDatabaseAsync(cancellationToken);

        await MigratorGate.WaitAsync(cancellationToken);
        try
        {
            await MigrationRunner.RunAsync(owner, TenancyRolePassword, EntitlementsRolePassword, cancellationToken);
        }
        finally
        {
            MigratorGate.Release();
        }

        var service = new NpgsqlConnectionStringBuilder(owner)
        {
            Username = EntitlementsModule.DatabaseRole,
            Password = EntitlementsRolePassword,
        }.ConnectionString;

        return new EntitlementsHarness(owner, service, configure);
    }

    /// <summary>No Docker: any connection attempt fails.</summary>
    public static EntitlementsHarness CreateUnreachable(Action<IServiceCollection>? configure = null) =>
        new(UnreachableConnectionString, UnreachableConnectionString, configure);

    public IServiceScope Scope(TenantResolution ambient)
    {
        var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<TestCurrentTenant>().Resolution = ambient;
        return scope;
    }

    public async Task<bool> IsEnabledAsync(TenantResolution ambient, FeatureKey feature, CancellationToken cancellationToken)
    {
        using var scope = Scope(ambient);
        return await scope.ServiceProvider.GetRequiredService<IEntitlementService>().IsEnabledAsync(feature, cancellationToken);
    }

    public Task<bool> IsEnabledAsync(TenantId tenant, FeatureKey feature, CancellationToken cancellationToken) =>
        IsEnabledAsync(TenantResolution.For(tenant), feature, cancellationToken);

    /// <summary>The service with a custom catalog (the only test seam: the real catalog has no second Pro-only key).</summary>
    public async Task<bool> IsEnabledWithCatalogAsync(
        TenantId tenant, FeatureKey feature, PlanCatalog catalog, CancellationToken cancellationToken)
    {
        using var scope = Scope(TenantResolution.For(tenant));
        var service = new EntitlementService(
            scope.ServiceProvider.GetRequiredService<EntitlementsDbContext>(),
            scope.ServiceProvider.GetRequiredService<ICurrentTenant>(),
            catalog,
            Clock);
        return await service.IsEnabledAsync(feature, cancellationToken);
    }

    public async Task<Result> StartTrialAsync(StartTrial command, CancellationToken cancellationToken, TenantResolution? ambient = null)
    {
        using var scope = Scope(ambient ?? TenantResolution.NoTenant);
        return await scope.ServiceProvider.GetRequiredService<StartTrialHandler>().HandleAsync(command, cancellationToken);
    }

    public async Task<Result> GrantAsync(GrantOverride command, CancellationToken cancellationToken, TenantResolution? ambient = null)
    {
        using var scope = Scope(ambient ?? TenantResolution.NoTenant);
        return await scope.ServiceProvider.GetRequiredService<GrantOverrideHandler>().HandleAsync(command, cancellationToken);
    }

    public async Task<Result> RevokeAsync(RevokeOverride command, CancellationToken cancellationToken, TenantResolution? ambient = null)
    {
        using var scope = Scope(ambient ?? TenantResolution.NoTenant);
        return await scope.ServiceProvider.GetRequiredService<RevokeOverrideHandler>().HandleAsync(command, cancellationToken);
    }

    /// <summary>Options for a context that reads and seeds directly: always the owner connection, never the handlers' role.</summary>
    public DbContextOptions<EntitlementsDbContext> NewOptions(Action<DbContextOptionsBuilder<EntitlementsDbContext>>? configure = null)
    {
        var builder = new DbContextOptionsBuilder<EntitlementsDbContext>();
        EntitlementsDbContextOptions.Configure(builder, _ownerConnectionString);
        configure?.Invoke(builder);
        return builder.Options;
    }

    /// <summary>Options as the handlers get them from DI: the service connection (the real role under <see cref="CreateAsRoleAsync"/>).</summary>
    public DbContextOptions<EntitlementsDbContext> NewServiceOptions(Action<DbContextOptionsBuilder<EntitlementsDbContext>>? configure = null)
    {
        var builder = new DbContextOptionsBuilder<EntitlementsDbContext>();
        EntitlementsDbContextOptions.Configure(builder, _serviceConnectionString);
        configure?.Invoke(builder);
        return builder.Options;
    }

    /// <summary>A context for direct reads and seeding, under a fixed ambient tenant. Never the handlers' own context.</summary>
    public EntitlementsDbContext Context(TenantId tenant) =>
        new(NewOptions(), new TestCurrentTenant { Resolution = TenantResolution.For(tenant) });

    public async Task<List<FeatureOverride>> OverridesOfAsync(TenantId tenant, CancellationToken cancellationToken)
    {
        await using var db = Context(tenant);
        return await db.FeatureOverrides.AsNoTracking().ToListAsync(cancellationToken);
    }

    public async Task<List<TrialGrant>> TrialsOfAsync(TenantId tenant, CancellationToken cancellationToken)
    {
        await using var db = Context(tenant);
        return await db.TrialGrants.AsNoTracking().ToListAsync(cancellationToken);
    }

    /// <summary>Every row count of the whole database, for "no row written anywhere" checks.</summary>
    public async Task<int> TotalRowsAsync(CancellationToken cancellationToken)
    {
        var total = 0;
        foreach (var tenant in new[] { TenantA, TenantB, TenantC })
        {
            total += (await OverridesOfAsync(tenant, cancellationToken)).Count;
            total += (await TrialsOfAsync(tenant, cancellationToken)).Count;
        }

        return total;
    }

    /// <summary>
    /// Every audit record of one tenant, read as the owner (the application role cannot SELECT the
    /// table), through plain Npgsql: the entity type is internal to the Audit module.
    /// </summary>
    public async Task<List<AuditRow>> AuditRowsOfAsync(TenantId tenant, CancellationToken cancellationToken)
    {
        var rows = await AuditRowsAsync(cancellationToken);
        return rows.Where(r => r.TenantId == tenant.Value).ToList();
    }

    /// <summary>Every audit record in the database, any tenant, read as the owner.</summary>
    public async Task<List<AuditRow>> AuditRowsAsync(CancellationToken cancellationToken)
    {
        var rows = new List<AuditRow>();
        await using var connection = new NpgsqlConnection(_ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT id, tenant_id, occurred_at, actor_user_id, action, outcome, feature_key, trace_id FROM audit.audit_records ORDER BY id",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AuditRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                Instant.FromDateTimeOffset(reader.GetFieldValue<DateTimeOffset>(2)),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return rows;
    }

    /// <summary>Runs one statement as the owner (fault injection such as a REVOKE).</summary>
    public async Task<int> ExecuteAsOwnerAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
#pragma warning disable CA2100 // every call site passes a fixed literal.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The first column of every row of one owner-run query, as text (schema introspection).</summary>
    public async Task<List<string>> QueryAsOwnerAsync(string sql, CancellationToken cancellationToken)
    {
        var values = new List<string>();
        await using var connection = new NpgsqlConnection(_ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
#pragma warning disable CA2100 // every call site passes a fixed literal.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    /// <summary>
    /// Disposes the provider and closes this database's pooled connections (both users): every harness has
    /// its own database, so its pools would otherwise idle until the shared container hits max_connections
    /// (53300 "too many clients already").
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();

        foreach (var connectionString in new[] { _ownerConnectionString, _serviceConnectionString }.Distinct())
        {
            await using var connection = new NpgsqlConnection(connectionString);
            NpgsqlConnection.ClearPool(connection);
        }
    }
}

/// <summary>One row of <c>audit.audit_records</c>, as the owner reads it.</summary>
internal sealed record AuditRow(
    Guid Id, Guid TenantId, Instant OccurredAt, string ActorUserId, string Action, string Outcome, string? FeatureKey, string? TraceId);
