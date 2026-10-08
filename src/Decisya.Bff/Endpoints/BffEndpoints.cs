using Decisya.Bff.RateLimiting;
using Decisya.Bff.Security;
using Decisya.Bff.Session;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using HeaderNames = Microsoft.Net.Http.Headers.HeaderNames;

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
        }).WithMetadata(new RouteClassMetadata(RouteClass.Login));
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
        }).WithMetadata(new RouteClassMetadata(RouteClass.Api));
    }

    private static void MapLogout(RouteGroupBuilder group)
    {
        group.MapPost("/logout", async (
            HttpContext context,
            RedisTicketStore ticketStore,
            RedisRefreshLock refreshLock,
            KeycloakTokenClient tokenClient,
            IOptionsMonitor<BffOptions> bffOptions,
            IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
            CancellationToken cancellationToken) =>
        {
            // B-2 (S-4, T-14): the lock is held across the end-session call and the ticket
            // delete below, so a refresh in flight can neither send Keycloak a refresh token
            // this call is about to end, nor write a refreshed ticket back after it is gone.
            // A lock the caller could not acquire in time is not fatal (G1 decision 2,
            // fail-open): the local logout still completes.
            var sessionKey = context.Features.Get<SessionKeyFeature>()?.Key;
            var signingOutPrincipal = context.User;
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

                // #121 G2 D4: the local session is gone (a Keycloak-side failure above never blocked it).
                AuthEvents.SignOut(context, "user", signingOutPrincipal);
            }
            finally
            {
                if (lockHandle is not null)
                {
                    await lockHandle.DisposeAsync().ConfigureAwait(false);
                }
            }

            // D3 of #18: no id_token_hint (OidcOptionsSetup's OnRedirectToIdentityProviderForSignOut
            // clears it). #26 D4: RedirectUri is "/" (the signed-out callback lands the browser on
            // the shell); post_logout_redirect_uri stays the realm-registered /signout-callback-oidc.
            // The handler (RedirectGet, pinned by a test) writes a 302 with the end-session URL.
            var properties = new AuthenticationProperties { RedirectUri = ReturnUrlValidator.Default };
            await context.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme, properties).ConfigureAwait(false);

            context.Response.Headers.Append(HeaderNames.Vary, HeaderNames.Accept);
            if (!WantsJson(context.Request))
            {
                // #18 Story 6: every other caller keeps the handler's own 302 + Location.
                return Results.Empty;
            }

            var location = context.Response.Headers.Location.ToString();
            EnsureEndSessionLocation(
                context.Response.StatusCode,
                location,
                bffOptions.CurrentValue.Oidc.Authority,
                oidcOptions.Get(OpenIdConnectDefaults.AuthenticationScheme).RequireHttpsMetadata);

            context.Response.Headers.Location = StringValues.Empty;
            return Results.Json(new LogoutResponse(location), statusCode: StatusCodes.Status200OK);
        }).RequireAuthorization().WithMetadata(new RouteClassMetadata(RouteClass.Login));
    }

    /// <summary>
    /// True only when <c>Accept</c> names exactly <c>application/json</c> with a non-zero quality.
    /// <c>*/*</c>, <c>application/*</c> and <c>application/problem+json</c> do not count, so a
    /// browser form post or any client with no <c>Accept</c> header keeps the 302 (#26 D4).
    /// </summary>
    internal static bool WantsJson(HttpRequest request)
    {
        foreach (var mediaType in request.GetTypedHeaders().Accept)
        {
            if (mediaType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                && (mediaType.Quality ?? 1.0) > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Re-checks the handler's own <c>Location</c> before it becomes script-readable data: status
    /// 302, an absolute URI, https (http only where the existing Development + loopback relaxation
    /// applies) and the Authority's own scheme, host and port. Fails with a fixed message that never
    /// echoes the URL; the exception handler turns it into the generic 500. The session is already
    /// gone by then, the same "fail after local logout" behaviour as a ticket-delete failure (#18).
    /// </summary>
    internal static void EnsureEndSessionLocation(int statusCode, string location, string? authority, bool requireHttps)
    {
        if (statusCode != StatusCodes.Status302Found
            || !Uri.TryCreate(location, UriKind.Absolute, out var target)
            || !Uri.TryCreate(authority, UriKind.Absolute, out var issuer)
            || !(target.Scheme == Uri.UriSchemeHttps || (!requireHttps && target.Scheme == Uri.UriSchemeHttp))
            || !string.Equals(target.Scheme, issuer.Scheme, StringComparison.Ordinal)
            || !string.Equals(target.Host, issuer.Host, StringComparison.OrdinalIgnoreCase)
            || target.Port != issuer.Port)
        {
            throw new InvalidOperationException("The sign-out redirect did not target the configured identity provider.");
        }
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

            if (await ticketStore.RemoveAllForSessionIdAsync(result.SessionId).ConfigureAwait(false))
            {
                // #121 G2 D4: only a valid logout token that removed a session is an event.
                AuthEvents.SignOut(context, "backchannel", principal: null, subjectOverride: result.Subject);
            }

            return Results.Ok();
        })
        // S-4: never runs cookie authentication (Keycloak calls this server to server); the
        // one, explicit antiforgery opt-out (D4).
        .AllowAnonymous()
        .WithMetadata(new SkipAntiforgeryMetadata(), new RouteClassMetadata(RouteClass.BackchannelLogout));
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
