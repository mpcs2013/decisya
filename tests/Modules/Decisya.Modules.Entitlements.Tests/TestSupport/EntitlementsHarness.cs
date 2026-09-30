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

namespace Decisya.Modules.Entitlements.Tests.TestSupport;

/// <summary>
/// The real module, wired the way the API wires it (<c>AddEntitlementsModule</c>), over either
/// a real Postgres 18 database or a placeholder host that throws on any connection attempt
/// (G3: "No database access is proved with a placeholder <c>Host=db.invalid</c>"). Every call
/// runs in its own DI scope with its own <see cref="TestCurrentTenant"/>, so each one is what a
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

    private readonly ServiceProvider _provider;
    private readonly string _connectionString;

    private EntitlementsHarness(string connectionString)
    {
        _connectionString = connectionString;
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
        services.AddEntitlementsModule(connectionString);
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public FakeClock Clock { get; }

    public CapturingLoggerProvider Logs { get; }

    /// <summary>A fresh database with the model created (<c>EnsureCreated</c>, so the unique indexes and check constraints are there).</summary>
    public static async Task<EntitlementsHarness> CreateAsync(PostgresFixture pg, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pg);

        var connectionString = await pg.CreateEmptyDatabaseAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<EntitlementsDbContext>();
        EntitlementsDbContextOptions.Configure(options, connectionString);
        await using (var db = new EntitlementsDbContext(options.Options, new TestCurrentTenant()))
        {
            await db.Database.EnsureCreatedAsync(cancellationToken);
        }

        return new EntitlementsHarness(connectionString);
    }

    /// <summary>No Docker: any connection attempt fails.</summary>
    public static EntitlementsHarness CreateUnreachable() => new(UnreachableConnectionString);

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

    public DbContextOptions<EntitlementsDbContext> NewOptions(Action<DbContextOptionsBuilder<EntitlementsDbContext>>? configure = null)
    {
        var builder = new DbContextOptionsBuilder<EntitlementsDbContext>();
        EntitlementsDbContextOptions.Configure(builder, _connectionString);
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

    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();
}
