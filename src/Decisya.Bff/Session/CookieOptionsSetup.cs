using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace Decisya.Bff.Session;

/// <summary>
/// The BFF's own session cookie (G2 "Cookie scheme" table): <c>__Host-decisya-session</c>,
/// <c>HttpOnly</c>, <c>Secure</c>, <c>SameSite=Strict</c>, no <c>Domain</c>, a 10 h absolute
/// (non-sliding) expiration matching Keycloak's <c>ssoSessionMaxLifespan</c> (D2), and a
/// browser-session cookie (<see cref="CookieAuthenticationOptions.Cookie"/>'s
/// <c>IsEssential</c> aside, no persistent <c>Expires</c>). <see cref="RedisTicketStore"/> is
/// wired in as the <see cref="CookieAuthenticationOptions.SessionStore"/> here, via
/// <see cref="IPostConfigureOptions{TOptions}"/> so it runs after the framework's own cookie
/// defaults (G2: "set through IPostConfigureOptions&lt;CookieAuthenticationOptions&gt;").
/// </summary>
internal sealed class CookieOptionsSetup(RedisTicketStore ticketStore) : IPostConfigureOptions<CookieAuthenticationOptions>
{
    internal const string SessionCookieName = "__Host-decisya-session";

    public void PostConfigure(string? name, CookieAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Cookie.Name = SessionCookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.Path = "/";
        options.Cookie.Domain = null;
        options.Cookie.IsEssential = true;

        // D2: absolute, non-sliding 10 h expiration; no cookie Expires/Max-Age persistence
        // (a browser-session cookie), so nothing outlives Keycloak's own SSO session ceiling.
        options.ExpireTimeSpan = TimeSpan.FromHours(10);
        options.SlidingExpiration = false;

        options.SessionStore = ticketStore;

        // Anonymous callers to a protected endpoint get 401/403, never a login redirect
        // (G2 "Anonymous callers to protected endpoints"); only GET /bff/login starts a
        // real challenge.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    }
}
