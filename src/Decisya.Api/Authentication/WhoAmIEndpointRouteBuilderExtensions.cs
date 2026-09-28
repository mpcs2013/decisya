using System.Security.Claims;

namespace Decisya.Api.Authentication;

/// <summary>
/// <c>GET /api/whoami</c> (D1): a minimal, permanent diagnostic endpoint. It relies entirely on
/// the fallback policy for authorization (no metadata of its own — G2), reads only the
/// validated principal through <see cref="CallerIdentity"/>, and touches no database and no
/// module.
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
        });

        return endpoints;
    }
}
