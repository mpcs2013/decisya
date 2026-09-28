using System.Net;
using System.Text.Json;

namespace Decisya.Bff.Tests;

/// <summary>
/// #87: the AppHost hands the BFF a Redis connection string with the literal host
/// <c>localhost</c> (never an IP), the same shape <see cref="RedisFixture.LocalhostConnectionString"/>
/// reproduces here. On a machine where <c>localhost</c> resolves to <c>::1</c> before
/// <c>127.0.0.1</c> and the Redis container only publishes on the IPv4 loopback, the unpatched
/// <c>RedisRegistration</c> connect attempt exceeds its 1s <c>ConnectTimeout</c> and
/// <c>/signin-oidc</c> fails with a <c>RedisConnectionException</c> (500) instead of completing
/// the login. If this test machine's resolver returns IPv4 first, this red run will not
/// reproduce the failure — the unit tests in <see cref="RedisLocalhostEndpointRewriteTests"/>
/// are the reliable red for the rewrite itself.
/// </summary>
[Trait("Category", "Integration")]
public class RedisLocalhostLoginTests
{
    private readonly KeycloakBffFixture _keycloakFixture;
    private readonly RedisFixture _redisFixture;

    public RedisLocalhostLoginTests(KeycloakBffFixture keycloakFixture, RedisFixture redisFixture)
    {
        _keycloakFixture = keycloakFixture;
        _redisFixture = redisFixture;
    }

    [Fact]
    public async Task Signing_in_through_a_localhost_Redis_connection_string_succeeds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);

        using var factory = BffFactoryFactory.Create(
            _keycloakFixture, _redisFixture, redisConnectionString: _redisFixture.LocalhostConnectionString);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);

        var callback = result.Exchanges.Single(exchange => exchange.Step == "bff-signin-oidc-callback");
        callback.StatusCode.Should().Be(
            HttpStatusCode.Found, "the callback must redirect to the return URL, not fail storing the ticket in Redis");

        using var meResponse = await result.BffClient.GetAsync("/bff/me", cancellationToken);
        meResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await meResponse.Content.ReadAsStringAsync(cancellationToken));
        document.RootElement.GetProperty("isAuthenticated").GetBoolean().Should().BeTrue();
    }
}
