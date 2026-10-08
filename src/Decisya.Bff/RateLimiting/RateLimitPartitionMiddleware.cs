using Decisya.Bff.Session;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace Decisya.Bff.RateLimiting;

/// <summary>
/// The partition step (#122, G2 D1), pipeline step 5. It classifies the request and works out what the
/// limiter counts it under, then stores a <see cref="RateLimitPartitionFeature"/> for the limiter that
/// runs right after it.
/// </summary>
/// <remarks>
/// For <c>api</c> and <c>admin</c> only it awaits the cookie scheme's <c>AuthenticateAsync</c>: the
/// handler caches its result per request, so <c>UseAuthentication</c> later reuses it (one Redis read,
/// not two; spike R5). For <c>login</c> and <c>backchannel_logout</c> it never authenticates, so nothing
/// populates the cookie handler on the OIDC callback and the session-fixation premise (T-05) holds. A
/// failed or missing session falls back to the client address; nothing here ever grants access.
/// </remarks>
internal sealed class RateLimitPartitionMiddleware(
    RequestDelegate next,
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    TrustedProxies trustedProxies,
    SessionPartitionHasher hasher)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var routeClass = RouteClassifier.Classify(context, oidcOptions.Get(OpenIdConnectDefaults.AuthenticationScheme));
        if (routeClass is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var (kind, value) = (PartitionKind.Unknown, "-");
        System.Security.Claims.ClaimsPrincipal? principal = null;

        if (routeClass is RouteClass.Api or RouteClass.Admin)
        {
            var result = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
            if (result.Succeeded && context.Features.Get<SessionKeyFeature>() is { } sessionKey)
            {
                (kind, value) = (PartitionKind.Session, hasher.Hash(sessionKey.Key));
                principal = result.Principal;
            }
        }

        if (kind != PartitionKind.Session)
        {
            (kind, value) = ClientPartition.FromAddress(context.Connection.RemoteIpAddress, trustedProxies.Addresses);
        }

        context.Features.Set(new RateLimitPartitionFeature(routeClass.Value, kind, value, principal));
        await next(context).ConfigureAwait(false);
    }
}
