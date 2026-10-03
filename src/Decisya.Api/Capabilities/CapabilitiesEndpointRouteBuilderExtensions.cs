using Decisya.Modules.Entitlements.Contracts;
using Decisya.SharedKernel.Authorization;

namespace Decisya.Api.Capabilities;

/// <summary>
/// <c>GET /api/capabilities</c> (issue #26; ADR-0008 amendment 1). Authorization is the API's
/// fallback policy only: no <c>RequireAuthorization()</c>, which would replace it with the default
/// policy and drop the <c>sub</c> requirement. The membership gate runs for tenant callers.
/// Keys are evaluated one at a time (one scoped DbContext per request, never concurrent), with no
/// try/catch: an exception reaches the exception handler as the generic 500, and the body is
/// written only after the loop, so a partial manifest cannot exist (#23 C-3, NFR-41).
/// <c>Cache-Control: no-store</c> comes from <c>UseNoStoreResponses</c>.
/// </summary>
public static class CapabilitiesEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapCapabilities(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/capabilities", async (IEntitlementService entitlements, CancellationToken cancellationToken) =>
        {
            var capabilities = new Dictionary<string, bool>(FeatureKeys.All.Count, StringComparer.Ordinal);
            foreach (var key in FeatureKeys.All)
            {
                capabilities[key.Value] = await entitlements.IsEnabledAsync(key, cancellationToken).ConfigureAwait(false);
            }

            return TypedResults.Ok(new CapabilitiesResponse { Capabilities = capabilities });
        })
        .WithMetadata(new NoEntitlementRequiredAttribute());

        return endpoints;
    }
}
