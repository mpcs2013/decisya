using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

namespace Decisya.Bff.Proxy;

/// <summary>
/// #19 G2: the whole YARP wiring for <c>/api/*</c> — one route, one cluster, both built in
/// code (<c>LoadFromMemory</c>), never from an <c>appsettings*.json</c> "ReverseProxy" section,
/// so no configuration source can add a route or a destination. The declarative transforms
/// (header removal, <c>X-Forwarded-*</c>) are expressed on the <see cref="RouteConfig"/>
/// itself; the two transforms that need per-request state
/// (<see cref="AttachAccessTokenRequestTransform"/>, <see cref="ReplaceUpstreamErrorBodyResponseTransform"/>)
/// are added through <c>AddTransforms</c>.
/// </summary>
internal static class ProxyConfiguration
{
    internal const string ApiRouteId = "api";
    internal const string ApiClusterId = "decisya-api";

    // Not literally "default": YARP reserves that exact policy name to mean "the framework's
    // own default policy, unset", and refuses to start if an application-registered policy
    // uses it (G2 calls this policy "default" in prose; this is the same authenticated-user
    // policy under a name YARP does not treat specially).
    internal const string DefaultAuthorizationPolicyName = "decisya-api-authenticated";

    internal static IReverseProxyBuilder AddApiReverseProxy(this IServiceCollection services, string apiAddress)
    {
        ArgumentNullException.ThrowIfNull(services);

        var routes = new[] { BuildRoute() };
        var clusters = new[] { BuildCluster(apiAddress) };

        return services.AddReverseProxy()
            .LoadFromMemory(routes, clusters)
            .AddServiceDiscoveryDestinationResolver()
            .AddTransforms(context =>
            {
                if (!string.Equals(context.Route.RouteId, ApiRouteId, StringComparison.Ordinal))
                {
                    return;
                }

                var environment = context.Services.GetRequiredService<IHostEnvironment>();
                context.RequestTransforms.Add(new AttachAccessTokenRequestTransform(environment));
                context.ResponseTransforms.Add(new ReplaceUpstreamErrorBodyResponseTransform());
            });
    }

    private static RouteConfig BuildRoute() =>
        new RouteConfig
        {
            RouteId = ApiRouteId,
            ClusterId = ApiClusterId,
            AuthorizationPolicy = DefaultAuthorizationPolicyName,
            Match = new RouteMatch
            {
                Path = "/api/{**catch-all}",
                Methods = ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE"],
            },
        }
        // T-01: the session, af and xsrf cookies never leave the BFF.
        .WithTransformRequestHeaderRemove("Cookie")
        // T-01: the antiforgery header served its purpose in ApiAntiforgeryMiddleware already.
        .WithTransformRequestHeaderRemove("X-XSRF-TOKEN")
        // T-01: a client-supplied RFC 7239 header is dropped; the BFF sets no replacement.
        .WithTransformRequestHeaderRemove("Forwarded")
        // T-01: "Set" (the default action here), not "Append" — a spoofed X-Forwarded-For (or
        // -Host/-Proto/-Prefix) from the client is replaced outright, never appended to.
        .WithTransformXForwarded()
        // T-04: the API must never set a cookie on the BFF's own origin.
        .WithTransformResponseHeaderRemove("Set-Cookie", ResponseCondition.Always);

    private static ClusterConfig BuildCluster(string apiAddress) =>
        new()
        {
            ClusterId = ApiClusterId,
            Destinations = new Dictionary<string, DestinationConfig>
            {
                [ApiClusterId] = new DestinationConfig { Address = apiAddress },
            },
        };
}
