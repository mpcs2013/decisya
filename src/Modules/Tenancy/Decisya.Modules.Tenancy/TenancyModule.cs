using System.Diagnostics;
using System.Diagnostics.Metrics;
using Decisya.Modules.Tenancy.Application;
using Decisya.Modules.Tenancy.Contracts;
using Decisya.Modules.Tenancy.Endpoints;
using Decisya.Modules.Tenancy.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Decisya.Modules.Tenancy;

/// <summary>Registers the Tenancy module (schema <c>tenancy</c>; issue #21, G2).</summary>
public static class TenancyModule
{
    /// <summary>Name of the module's <see cref="ActivitySource"/> and <see cref="Meter"/>.</summary>
    public const string TelemetryName = "Decisya.Tenancy";

    /// <summary>The module's Postgres schema.</summary>
    public const string Schema = "tenancy";

    /// <summary>The least-privilege database role the API connects as (G2, D4).</summary>
    public const string DatabaseRole = "decisya_tenancy";

    internal static readonly ActivitySource ActivitySource = new(TelemetryName);

    internal static readonly Meter Meter = new(TelemetryName);

    /// <summary>
    /// Adds the Tenancy module's services: <see cref="TenancyDbContext"/> (plain
    /// <c>AddDbContext</c>, never pooled — the #22 constructor convention forbids pooling),
    /// the JIT provisioning gate and the <c>Tenancy.Owner</c> authorization policy.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="connectionString"/> is null or blank. Never includes the connection string itself in the message.</exception>
    public static IServiceCollection AddTenancyModule(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Missing required configuration key: ConnectionStrings:tenancy.");
        }

        services.AddDbContext<TenancyDbContext>(
            options => TenancyDbContextOptions.Configure(options, connectionString));

        services.AddScoped<TenantMembershipGate>();
        services.AddScoped<ITenantExistence, TenantExistence>();

        services.AddAuthorizationBuilder()
            .AddPolicy(TenancyPolicies.OwnerPolicyName, policy => policy.Requirements.Add(new TenantOwnerRequirement()));
        services.AddScoped<IAuthorizationHandler, TenantOwnerAuthorizationHandler>();

        return services;
    }

    /// <summary>JIT provisioning for every authorized endpoint that has not opted out (G2, D2). Must run after <c>UseRouting</c> and before <c>UseAuthorization</c>.</summary>
    public static IApplicationBuilder UseTenancyMembership(this IApplicationBuilder app) =>
        TenantMembershipMiddleware.Use(app);

    /// <summary>Maps <c>GET /api/tenancy/me</c> and <c>GET /api/tenancy/members</c> (Stories 2 and 3).</summary>
    public static IEndpointRouteBuilder MapTenancyEndpoints(this IEndpointRouteBuilder endpoints) =>
        TenancyEndpoints.Map(endpoints);

    /// <summary>Opts an endpoint out of <see cref="UseTenancyMembership"/> (as of #21, only <c>/api/whoami</c>).</summary>
    public static TBuilder SkipTenantMembership<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(SkipTenantMembershipMetadata.Instance);
        return builder;
    }
}
