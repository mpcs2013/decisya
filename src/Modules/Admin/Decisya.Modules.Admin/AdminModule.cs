using System.Diagnostics;
using System.Diagnostics.Metrics;
using Decisya.Modules.Admin.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Decisya.Modules.Admin;

/// <summary>
/// Registers the Admin module (issue #25, G2 D3): the platform-admin HTTP surface under
/// <c>/api/admin</c>. The module persists nothing and calls other modules only through their
/// Contracts.
/// </summary>
public static class AdminModule
{
    /// <summary>Name of the module's <see cref="ActivitySource"/> and <see cref="Meter"/>.</summary>
    public const string TelemetryName = "Decisya.Admin";

    /// <summary>The authorization policy on the <c>/api/admin</c> group: authenticated, a <c>sub</c> claim, and a tenant-less platform admin.</summary>
    public const string PlatformAdminPolicy = "Admin.PlatformAdmin";

    internal static readonly ActivitySource ActivitySource = new(TelemetryName);

    internal static readonly Meter Meter = new(TelemetryName);

    /// <summary>
    /// Adds the <see cref="PlatformAdminPolicy"/> policy and its handler. Takes no connection string:
    /// the module has no database.
    /// </summary>
    public static IServiceCollection AddAdminModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The policy replaces the fallback policy on the group, so it repeats the fallback's two
        // requirements (an authenticated user with a sub claim) before its own.
        services.AddAuthorizationBuilder()
            .AddPolicy(PlatformAdminPolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim("sub")
                .AddRequirements(new PlatformAdminRequirement()));
        services.AddScoped<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();

        return services;
    }

    /// <summary>
    /// Maps the three admin routes under <c>/api/admin</c> and returns the group, which already
    /// requires <see cref="PlatformAdminPolicy"/>. The host applies the tenant-membership opt-out
    /// to the returned group (<c>SkipTenantMembership</c>; this module may not reference Tenancy).
    /// </summary>
    public static RouteGroupBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints) =>
        AdminEndpoints.Map(endpoints);
}
