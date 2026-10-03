using System.Net;

namespace Decisya.Bff.Tests;

/// <summary>
/// #26 G2 D6 and G3 S-b through the real YARP route: the security headers are on a proxied
/// <c>/api</c> response, an upstream cannot loosen them, and the Api's own 404 passes through (the
/// SPA fallback never answers a real <c>/api</c> GET).
/// </summary>
[Trait("Category", "Integration")]
public class SpaProxiedResponseTests
{
    private readonly KeycloakBffFixture _keycloakFixture;
    private readonly RedisFixture _redisFixture;

    public SpaProxiedResponseTests(KeycloakBffFixture keycloakFixture, RedisFixture redisFixture)
    {
        _keycloakFixture = keycloakFixture;
        _redisFixture = redisFixture;
    }

    [Fact]
    public async Task A_proxied_api_response_carries_the_BFF_policy_whatever_the_upstream_sent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);
        await using var apiDouble = await ApiDouble.StartAsync(cancellationToken);
        apiDouble.RespondWithWeakSecurityHeaders = true;
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, apiAddress: apiDouble.Address);
        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/", cancellationToken);

        using var response = await result.BffClient.GetAsync("/api/capabilities", cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        apiDouble.Requests.Should().ContainSingle();
        response.Headers.GetValues("Content-Security-Policy").Should().ContainSingle().Which.Should()
            .StartWith("default-src 'self'; script-src 'self'");
        response.Headers.GetValues("X-Frame-Options").Should().ContainSingle().Which.Should().Be("DENY");
        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");
        response.Headers.GetValues("Referrer-Policy").Should().ContainSingle().Which.Should().Be("no-referrer");
        response.Headers.GetValues("Cross-Origin-Resource-Policy").Should().ContainSingle().Which.Should().Be("same-origin");
        response.Headers.GetValues("Cross-Origin-Opener-Policy").Should().ContainSingle().Which.Should().Be("same-origin");
        response.Headers.CacheControl!.NoStore.Should().BeTrue("nothing set Cache-Control, so the default applies");
    }

    [Fact]
    public async Task An_unknown_api_path_is_forwarded_and_never_answered_with_the_shell()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);
        await using var apiDouble = await ApiDouble.StartAsync(cancellationToken);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, apiAddress: apiDouble.Address);
        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/", cancellationToken);

        using var response = await result.BffClient.GetAsync("/api/does-not-exist", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        apiDouble.Requests.Should().ContainSingle().Which.Path.Should().Be("/api/does-not-exist");
        response.Content.Headers.ContentType?.MediaType.Should().NotBe("text/html");
        body.Should().NotContain("<html");
    }
}
