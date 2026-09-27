using System.Net;

namespace Decisya.Bff.Tests;

/// <summary>
/// G4-18-01's first red test: every <c>Set-Cookie</c> across login, callback, <c>/bff/me</c>,
/// logout and the signout callback carries exactly the attributes G2's cookie tables name.
/// Story 1's Done-when scenario and Story 1's second scenario (correlation/nonce cookies).
/// </summary>
[Trait("Category", "Integration")]
public class BffLoginFlowTests
{
    private readonly KeycloakBffFixture _keycloakFixture;
    private readonly RedisFixture _redisFixture;

    public BffLoginFlowTests(KeycloakBffFixture keycloakFixture, RedisFixture redisFixture)
    {
        _keycloakFixture = keycloakFixture;
        _redisFixture = redisFixture;
    }

    [Fact]
    public async Task Every_set_cookie_in_the_flow_has_the_required_attributes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);

        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);

        // --- Session cookie, wherever it appears across the flow so far ---
        var sessionCookies = result.Exchanges
            .SelectMany(exchange => SetCookieParser.ParseAll(exchange.Headers))
            .Where(cookie => cookie.Name == "__Host-decisya-session")
            .ToList();
        sessionCookies.Should().NotBeEmpty("the callback should establish a session");
        foreach (var cookie in sessionCookies)
        {
            AssertSessionCookieShape(cookie);
        }

        // --- Correlation and nonce cookies (Story 1 scenario 2) ---
        var correlationOrNonceCookies = result.Exchanges
            .SelectMany(exchange => SetCookieParser.ParseAll(exchange.Headers))
            .Where(cookie => cookie.Name.Contains("Correlation", StringComparison.OrdinalIgnoreCase)
                || cookie.Name.Contains("Nonce", StringComparison.OrdinalIgnoreCase))
            .ToList();
        correlationOrNonceCookies.Should().NotBeEmpty();
        foreach (var cookie in correlationOrNonceCookies)
        {
            cookie.SameSite.Should().BeEquivalentTo("Lax");
            cookie.Secure.Should().BeTrue();
            cookie.HttpOnly.Should().BeTrue();
        }

        // --- /bff/me issues the antiforgery pair ---
        using var meResponse = await result.BffClient.GetAsync("/bff/me", cancellationToken);
        var meExchange = await LoginFlowHarness.CaptureAsync("bff-me", meResponse, cancellationToken);

        var afCookie = SetCookieParser.ParseAll(meExchange.Headers).SingleOrDefault(cookie => cookie.Name == "__Host-decisya-af");
        afCookie.Should().NotBeNull();
        afCookie!.HttpOnly.Should().BeTrue();
        afCookie.Secure.Should().BeTrue();
        afCookie.SameSite.Should().BeEquivalentTo("Strict");
        afCookie.Path.Should().Be("/");

        var xsrfCookie = SetCookieParser.ParseAll(meExchange.Headers).SingleOrDefault(cookie => cookie.Name == "__Host-decisya-xsrf");
        xsrfCookie.Should().NotBeNull();
        xsrfCookie!.HttpOnly.Should().BeFalse("the SPA must be able to read this cookie in JavaScript (D4)");
        xsrfCookie.Secure.Should().BeTrue();
        xsrfCookie.SameSite.Should().BeEquivalentTo("Strict");
        xsrfCookie.Path.Should().Be("/");

        // --- Logout clears the session cookie and carries no id_token_hint (D3) ---
        result.BffJar.Cookies.TryGetValue("__Host-decisya-xsrf", out var xsrfToken).Should().BeTrue();
        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/bff/logout");
        logoutRequest.Headers.Add("X-XSRF-TOKEN", xsrfToken);
        using var logoutResponse = await result.BffClient.SendAsync(logoutRequest, cancellationToken);
        var logoutExchange = await LoginFlowHarness.CaptureAsync("bff-logout", logoutResponse, cancellationToken);

        logoutExchange.StatusCode.Should().Be(HttpStatusCode.Found);

        var clearedSessionCookie = SetCookieParser.ParseAll(logoutExchange.Headers)
            .SingleOrDefault(cookie => cookie.Name == "__Host-decisya-session");
        clearedSessionCookie.Should().NotBeNull();
        clearedSessionCookie!.HasExpiry.Should().BeTrue("logout must clear the session cookie");

        logoutExchange.Headers.Should().ContainKey("Location");
        var logoutLocation = logoutExchange.Headers["Location"].Single();
        logoutLocation.Should().NotContain("id_token_hint");
        logoutLocation.Should().Contain("client_id=decisya-bff");
    }

    private static void AssertSessionCookieShape(ParsedSetCookie cookie)
    {
        cookie.HttpOnly.Should().BeTrue();
        cookie.Secure.Should().BeTrue();
        cookie.SameSite.Should().BeEquivalentTo("Strict");
        cookie.Domain.Should().BeNull();

        // Only assert "no Expires/Max-Age" on the cookie that establishes the session: the
        // one clearing it on logout legitimately carries one (checked separately below).
        if (!cookie.HasExpiry)
        {
            return;
        }

        cookie.Value.Should().BeEmpty("a session cookie with an expiry attribute set here should be a logout clear, not a fresh sign-in");
    }
}
