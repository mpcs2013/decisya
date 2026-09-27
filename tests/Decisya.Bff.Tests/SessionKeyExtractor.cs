using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Decisya.Bff.Tests;

/// <summary>
/// Recovers the Redis session key from a raw <c>__Host-decisya-session</c> cookie value, the
/// same way <c>Decisya.Bff.Session.SessionFixationGuard</c> does, so tests can read the
/// ticket straight out of <c>RedisTicketStore</c> for the token-leak scan and the fixation
/// test. Test-only: never referenced by production code.
/// </summary>
internal static class SessionKeyExtractor
{
    internal static string? Extract(IServiceProvider services, string rawSessionCookieValue)
    {
        var cookieOptionsMonitor = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
        var cookieOptions = cookieOptionsMonitor.Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = cookieOptions.TicketDataFormat.Unprotect(rawSessionCookieValue);
        return ticket?.Principal.Claims.FirstOrDefault()?.Value;
    }
}
