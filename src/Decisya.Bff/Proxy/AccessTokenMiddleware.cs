using Decisya.Bff.Session;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Decisya.Bff.Proxy;

/// <summary>
/// #19 G2: asks <see cref="AccessTokenProvider"/> for a token to forward, and turns its result
/// into the three request outcomes G2 defines. Runs after <see cref="ApiAntiforgeryMiddleware"/>
/// and before YARP's own steps, so a token is never even requested for a request the
/// antiforgery check has already rejected, and nothing is forwarded unless a token is attached.
/// </summary>
/// <remarks><see cref="AccessTokenProvider"/> is an <c>InvokeAsync</c> parameter, not a
/// constructor one: <c>IApplicationBuilder.UseMiddleware&lt;T&gt;</c> resolves constructor
/// dependencies once, eagerly, from the root service provider, while the pipeline is being
/// built (<c>MapReverseProxy</c> does this at host start-up, not per request) — which would
/// force Redis to be reachable before the very first request. Framework convention-based
/// middleware resolves extra <c>InvokeAsync</c> parameters per request instead.</remarks>
internal sealed class AccessTokenMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AccessTokenProvider accessTokenProvider)
    {
        ArgumentNullException.ThrowIfNull(context);

        var result = await accessTokenProvider.GetAsync(context, context.RequestAborted).ConfigureAwait(false);

        switch (result.Status)
        {
            case AccessTokenStatus.Token:
                context.Features.Set(new ForwardedAccessTokenFeature(result.AccessToken!));
                await next(context).ConfigureAwait(false);
                return;

            case AccessTokenStatus.SessionEnded:
                // B-1: the ticket is already gone (or Keycloak just rejected the refresh);
                // SignOutAsync clears the cookie too, so the very next call also gets 401.
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;

            default: // Unavailable (D6): the session is kept; nothing is forwarded.
                await Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "The upstream service is temporarily unavailable.")
                    .ExecuteAsync(context)
                    .ConfigureAwait(false);
                return;
        }
    }
}
