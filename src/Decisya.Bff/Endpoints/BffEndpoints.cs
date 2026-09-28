using Decisya.Bff.Security;
using Decisya.Bff.Session;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http.Features;

namespace Decisya.Bff.Endpoints;

/// <summary>Maps every route under <c>/bff</c> (G2's endpoint table).</summary>
internal static class BffEndpoints
{
    private const string LogoutTokenFormField = "logout_token";
    private const long BackchannelLogoutMaxBodyBytes = 16 * 1024;

    public static IEndpointRouteBuilder MapBffEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/bff")
            .WithMetadata(new AntiforgeryRequiredMetadata())
            .AddEndpointFilter<AntiforgeryEndpointFilter>();

        MapLogin(group);
        MapMe(group);
        MapLogout(group);
        MapBackchannelLogout(group);

        return endpoints;
    }

    private static void MapLogin(RouteGroupBuilder group)
    {
        group.MapGet("/login", (HttpContext context, string? returnUrl) =>
        {
            var safeReturnUrl = ReturnUrlValidator.Sanitize(returnUrl);

            return context.User.Identity?.IsAuthenticated == true
                ? Results.LocalRedirect(safeReturnUrl)
                : Results.Challenge(
                    new AuthenticationProperties { RedirectUri = safeReturnUrl },
                    [OpenIdConnectDefaults.AuthenticationScheme]);
        });
    }

    private static void MapMe(RouteGroupBuilder group)
    {
        group.MapGet("/me", (HttpContext context, IAntiforgery antiforgery) =>
        {
            IssueXsrfCookie(context, antiforgery);

            if (context.User.Identity?.IsAuthenticated != true)
            {
                return Results.Json(MeResponse.Anonymous);
            }

            var principal = context.User;
            var roles = principal.FindAll("roles").Select(claim => claim.Value).ToList();

            return Results.Json(new MeResponse
            {
                IsAuthenticated = true,
                Sub = principal.FindFirst("sub")?.Value,
                Email = principal.FindFirst("email")?.Value,
                TenantId = principal.FindFirst("tenant_id")?.Value,
                Roles = roles,
            });
        });
    }

    private static void MapLogout(RouteGroupBuilder group)
    {
        group.MapPost("/logout", async (
            HttpContext context,
            RedisTicketStore ticketStore,
            RedisRefreshLock refreshLock,
            KeycloakTokenClient tokenClient,
            CancellationToken cancellationToken) =>
        {
            // B-2 (S-4, T-14): the lock is held across the end-session call and the ticket
            // delete below, so a refresh in flight can neither send Keycloak a refresh token
            // this call is about to end, nor write a refreshed ticket back after it is gone.
            // A lock the caller could not acquire in time is not fatal (G1 decision 2,
            // fail-open): the local logout still completes.
            var sessionKey = context.Features.Get<SessionKeyFeature>()?.Key;
            var lockHandle = sessionKey is null
                ? null
                : await refreshLock.AcquireOrWaitAsync(sessionKey, cancellationToken).ConfigureAwait(false);

            try
            {
                if (sessionKey is not null)
                {
                    var ticket = await ticketStore.RetrieveAsync(sessionKey).ConfigureAwait(false);
                    var refreshToken = ticket?.Properties.GetTokenValue("refresh_token");
                    if (!string.IsNullOrEmpty(refreshToken))
                    {
                        await tokenClient.EndSessionAsync(refreshToken, cancellationToken).ConfigureAwait(false);
                    }
                }

                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
            }
            finally
            {
                if (lockHandle is not null)
                {
                    await lockHandle.DisposeAsync().ConfigureAwait(false);
                }
            }

            // D3: no id_token_hint (set to null by OidcOptionsSetup's OnRedirectToIdentityProviderForSignOut).
            var properties = new AuthenticationProperties
            {
                RedirectUri = "/signout-callback-oidc",
            };

            return Results.SignOut(properties, [OpenIdConnectDefaults.AuthenticationScheme]);
        }).RequireAuthorization();
    }

    private static void MapBackchannelLogout(RouteGroupBuilder group)
    {
        group.MapPost("/backchannel-logout", async (
            HttpContext context, LogoutTokenValidator validator, RedisTicketStore ticketStore, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";

            var maxSizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (maxSizeFeature is { IsReadOnly: false })
            {
                maxSizeFeature.MaxRequestBodySize = BackchannelLogoutMaxBodyBytes;
            }

            if (!context.Request.HasFormContentType)
            {
                return Results.StatusCode(StatusCodes.Status400BadRequest);
            }

            string logoutToken;
            try
            {
                var form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
                logoutToken = form[LogoutTokenFormField].ToString();
            }
            catch (BadHttpRequestException)
            {
                return Results.StatusCode(StatusCodes.Status400BadRequest);
            }

            if (string.IsNullOrEmpty(logoutToken))
            {
                return Results.StatusCode(StatusCodes.Status400BadRequest);
            }

            var result = await validator.ValidateAsync(logoutToken, cancellationToken).ConfigureAwait(false);
            if (!result.IsValid || result.SessionId is null)
            {
                // S-4: the reason is a fixed code naming which rule failed, logged
                // server-side only — the response body still names no reason.
                BffLog.BackchannelLogoutTokenRejected(
                    context.RequestServices.GetRequiredService<ILogger<Program>>(), result.FailureReason ?? "unknown");
                return Results.StatusCode(StatusCodes.Status400BadRequest);
            }

            await ticketStore.RemoveAllForSessionIdAsync(result.SessionId).ConfigureAwait(false);
            return Results.Ok();
        })
        // S-4: never runs cookie authentication (Keycloak calls this server to server); the
        // one, explicit antiforgery opt-out (D4).
        .AllowAnonymous()
        .WithMetadata(new SkipAntiforgeryMetadata());
    }

    private static void IssueXsrfCookie(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        if (tokens.RequestToken is null)
        {
            return;
        }

        context.Response.Cookies.Append(AntiforgeryCookieNames.XsrfCookie, tokens.RequestToken, new CookieOptions
        {
            HttpOnly = false,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
        });
    }
}
