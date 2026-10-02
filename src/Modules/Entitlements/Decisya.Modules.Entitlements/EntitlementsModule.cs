using System.Diagnostics;
using System.Diagnostics.Metrics;
using Decisya.Modules.Entitlements.Application;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Entitlements.Contracts.Admin;
using Decisya.Modules.Entitlements.Domain;
using Decisya.Modules.Entitlements.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Decisya.Modules.Entitlements;

/// <summary>Registers the Entitlements module (schema <c>entitlements</c>; issue #23, G2).</summary>
public static class EntitlementsModule
{
    /// <summary>Name of the module's <see cref="ActivitySource"/> and <see cref="Meter"/>.</summary>
    public const string TelemetryName = "Decisya.Entitlements";

    /// <summary>The module's Postgres schema.</summary>
    public const string Schema = "entitlements";

    /// <summary>The least-privilege database role the API connects as (G2).</summary>
    public const string DatabaseRole = "decisya_entitlements";

    internal static readonly ActivitySource ActivitySource = new(TelemetryName);

    internal static readonly Meter Meter = new(TelemetryName);

    /// <summary>
    /// Adds <see cref="EntitlementsDbContext"/> (plain <c>AddDbContext</c>, never pooled: the #22
    /// constructor convention forbids pooling), the plan catalog, <see cref="IEntitlementService"/>
    /// the three admin handlers and their public facade (<see cref="IEntitlementAdminCommands"/>, #25). No endpoint: <c>Decisya.Modules.Admin</c> owns the HTTP side.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="connectionString"/> is null or blank. The message never includes the connection string itself.</exception>
    public static IServiceCollection AddEntitlementsModule(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Missing required configuration key: ConnectionStrings:entitlements.");
        }

        services.AddDbContext<EntitlementsDbContext>(
            options => EntitlementsDbContextOptions.Configure(options, connectionString));

        services.AddSingleton(PlanCatalog.Default);
        services.AddScoped<IEntitlementService, EntitlementService>();
        services.AddScoped<StartTrialHandler>();
        services.AddScoped<GrantOverrideHandler>();
        services.AddScoped<RevokeOverrideHandler>();
        services.AddScoped<IEntitlementAdminCommands, EntitlementAdminCommands>();

        return services;
    }
}
