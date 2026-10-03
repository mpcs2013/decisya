using System.Security.Claims;
using Decisya.Modules.Tenancy;
using Decisya.SharedKernel.Authorization;

namespace Decisya.Api.Authentication;

/// <summary>
/// <c>GET /api/whoami</c> (D1): a minimal, permanent diagnostic endpoint. It relies entirely on
/// the fallback policy for authorization (its only metadata is the ADR-0008
/// <see cref="NoEntitlementRequiredAttribute"/> marker, #26), reads only the
/// validated principal through <see cref="CallerIdentity"/>, and touches no database and no
/// module. The only explicit opt-out from the Tenancy membership gate (issue #21, G2; the
/// #20 no-Docker test harness keeps working, confirmed by Marco 2026-09-28): since #21,
/// <see cref="CallerContextMiddleware"/> already rejects an invalid identity or a malformed
/// <c>tenant_id</c> claim before this endpoint ever runs, so the check below is now unreachable
/// defense in depth rather than this endpoint's primary guard.
/// </summary>
public static class WhoAmIEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapWhoAmI(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/whoami", (ClaimsPrincipal user, HttpResponse response) =>
        {
            response.Headers.CacheControl = "no-store";

            var identity = CallerIdentity.From(user);
            if (identity is null)
            {
                // T-14/S-2: a valid token whose claims cannot be trusted as a single identity
                // (duplicate or blank sub/tenant_id) fails closed with a generic ProblemDetails,
                // never a claim value.
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden);
            }

            return Results.Ok(new WhoAmIResponse { UserId = identity.UserId, TenantId = identity.TenantId });
        })
        .WithMetadata(new NoEntitlementRequiredAttribute())
        .SkipTenantMembership();

        return endpoints;
    }
}
