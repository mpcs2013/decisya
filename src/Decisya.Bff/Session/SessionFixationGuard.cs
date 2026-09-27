using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Decisya.Bff.Session;

/// <summary>
/// G4-18-05 (T-05): a sign-in that arrives at <c>/signin-oidc</c> carrying an existing,
/// unrelated session cookie must never let that cookie's session key reach the cookie
/// handler's own <c>AuthenticateAsync</c> for this request — dotnet/aspnetcore#22135 means a
/// handler that has already resolved a ticket for this request renews the SAME session key
/// in place on the next <c>SignInAsync</c>, instead of minting a fresh one, which is exactly
/// session fixation: the attacker who planted the cookie already knows that key.
/// </summary>
/// <remarks>
/// Runs as ordinary middleware registered before <c>UseAuthentication()</c> (G3's suggested
/// shape), so it never calls <see cref="Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.AuthenticateAsync(HttpContext, string?)"/>
/// itself — doing so would populate the cookie handler's own state with the very cookie this
/// class exists to neutralise. Instead it: (1) unprotects the raw incoming session cookie
/// directly, with the same <see cref="Microsoft.AspNetCore.Authentication.ISecureDataFormat{TData}"/>
/// the cookie handler itself uses, to learn the old session key without ever authenticating
/// with it; (2) deletes that key's ticket from <see cref="RedisTicketStore"/> directly,
/// independent of whatever <see cref="ITicketStore"/> is currently attached to the cookie
/// scheme; (3) strips the cookie from the request's <c>Cookie</c> header, so
/// <c>UseAuthentication()</c>'s own call later in the pipeline finds nothing for the default
/// scheme, leaving the handler's key state unset for the rest of this request.
/// </remarks>
internal static class SessionFixationGuard
{
    public static async Task RemoveExistingSessionOnCallbackAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var oidcOptionsMonitor = context.RequestServices.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();
        var oidcOptions = oidcOptionsMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme);
        if (context.Request.Path != oidcOptions.CallbackPath)
        {
            return;
        }

        if (!context.Request.Cookies.TryGetValue(CookieOptionsSetup.SessionCookieName, out var rawCookieValue)
            || string.IsNullOrEmpty(rawCookieValue))
        {
            return;
        }

        var oldSessionKey = TryExtractSessionKey(context, rawCookieValue);
        if (oldSessionKey is not null)
        {
            var ticketStore = context.RequestServices.GetRequiredService<RedisTicketStore>();
            await ticketStore.RemoveAsync(oldSessionKey).ConfigureAwait(false);
        }

        RemoveCookieFromRequestHeader(context, CookieOptionsSetup.SessionCookieName);
    }

    private static string? TryExtractSessionKey(HttpContext context, string rawCookieValue)
    {
        try
        {
            var cookieOptionsMonitor = context.RequestServices.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
            var cookieOptions = cookieOptionsMonitor.Get(CookieAuthenticationDefaults.AuthenticationScheme);
            var ticket = cookieOptions.TicketDataFormat.Unprotect(rawCookieValue);

            // With a SessionStore configured, the cookie's own ticket carries exactly one
            // claim: the session key (CookieAuthenticationHandler's internal
            // "Microsoft.AspNetCore.Authentication.Cookies-SessionId" claim type).
            return ticket?.Principal.Claims.FirstOrDefault()?.Value;
        }
        catch (CryptographicException)
        {
            // An unprotectable or malformed cookie has no session to remove; fall through to
            // stripping it below regardless.
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static void RemoveCookieFromRequestHeader(HttpContext context, string cookieName)
    {
        if (!context.Request.Headers.TryGetValue(HeaderNames.Cookie, out var cookieHeaderValues) || cookieHeaderValues.Count == 0)
        {
            return;
        }

        var remainingPairs = new List<string>();
        foreach (var headerValue in cookieHeaderValues)
        {
            if (headerValue is null)
            {
                continue;
            }

            foreach (var pair in headerValue.Split(';'))
            {
                var trimmedPair = pair.Trim();
                if (trimmedPair.Length == 0)
                {
                    continue;
                }

                var separatorIndex = trimmedPair.IndexOf('=');
                var name = separatorIndex >= 0 ? trimmedPair[..separatorIndex] : trimmedPair;
                if (!string.Equals(name, cookieName, StringComparison.Ordinal))
                {
                    remainingPairs.Add(trimmedPair);
                }
            }
        }

        if (remainingPairs.Count == 0)
        {
            context.Request.Headers.Remove(HeaderNames.Cookie);
        }
        else
        {
            context.Request.Headers[HeaderNames.Cookie] = string.Join("; ", remainingPairs);
        }
    }
}
