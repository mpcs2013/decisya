using System.Security.Claims;
using Decisya.Bff.Session;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Decisya.Bff.Tests.RateLimiting;

/// <summary>A seeded session: the ticket lives in the Redis store, the cookie is protected by the host's own Data Protection.</summary>
internal sealed record SeededSession(string Cookie, string TicketKey, string Sid);

internal static class SessionSeeding
{
    internal const string CookieName = "__Host-decisya-session";
    private const string SessionIdClaim = "Microsoft.AspNetCore.Authentication.Cookies-SessionId";

    /// <summary>Stores a ticket through the real <see cref="RedisTicketStore"/> and protects the cookie through the host's cookie ticket format (the path a sign-in takes).</summary>
    internal static async Task<SeededSession> SeedAsync(RateLimitFactory factory, string? tenant = null, string? sid = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        sid ??= Canaries.Unique("sid");
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sid", sid),
                new Claim("sub", Canaries.Unique("sub")),
                new Claim("tenant_id", tenant ?? Guid.NewGuid().ToString("D")),
            ],
            "TestSeed"));
        var now = NodaTime.SystemClock.Instance.GetCurrentInstant();
        var properties = new AuthenticationProperties { ExpiresUtc = now.Plus(Duration.FromHours(1)).ToDateTimeOffset() };
        properties.StoreTokens(
        [
            new AuthenticationToken { Name = "access_token", Value = Canaries.Unique("access") },
            new AuthenticationToken { Name = "expires_at", Value = now.Plus(Duration.FromHours(1)).ToDateTimeOffset().ToString("o", System.Globalization.CultureInfo.InvariantCulture) },
        ]);

        var key = await factory.Services.GetRequiredService<RedisTicketStore>()
            .StoreAsync(new AuthenticationTicket(principal, properties, "Cookies"));

        var sessionTicket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(SessionIdClaim, key)], "Cookies")),
            new AuthenticationProperties { ExpiresUtc = now.Plus(Duration.FromHours(1)).ToDateTimeOffset() },
            "Cookies");
        var cookieOptions = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        return new SeededSession(CookieName + "=" + cookieOptions.TicketDataFormat.Protect(sessionTicket), key, sid);
    }
}
