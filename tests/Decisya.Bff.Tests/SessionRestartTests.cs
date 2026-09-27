using System.Text.Json;

namespace Decisya.Bff.Tests;

/// <summary>
/// G5 (issue #18 traceability): Story 2's third scenario and NFR-23. Disposing the first
/// <see cref="BffWebApplicationFactory"/> and building a second one on the same, on-disk key
/// ring directory stands in for a BFF process restart (G2's own test-harness note in
/// <see cref="BffFactoryFactory"/>); the ticket itself lives in the shared Redis container,
/// untouched by either factory's disposal.
/// </summary>
[Trait("Category", "Integration")]
public class SessionRestartTests
{
    private readonly KeycloakBffFixture _keycloakFixture;
    private readonly RedisFixture _redisFixture;

    public SessionRestartTests(KeycloakBffFixture keycloakFixture, RedisFixture redisFixture)
    {
        _keycloakFixture = keycloakFixture;
        _redisFixture = redisFixture;
    }

    [Fact]
    public async Task A_session_survives_a_BFF_process_restart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);
        var sharedKeyRingPath = BffFactoryFactory.CreateTemporaryKeyRingDirectory();

        string? sessionCookie;
        using (var firstFactory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, sharedKeyRingPath))
        {
            var result = await LoginFlowHarness.LogInAsync(
                firstFactory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);
            result.BffJar.Cookies.TryGetValue("__Host-decisya-session", out sessionCookie).Should().BeTrue();
        } // Disposing the factory here stands in for the BFF process stopping.

        using var secondFactory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, sharedKeyRingPath);
        using var client = LoginFlowHarness.CreateRawBffClient(secondFactory);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/bff/me");
        request.Headers.TryAddWithoutValidation("Cookie", $"__Host-decisya-session={sessionCookie}");
        using var response = await client.SendAsync(request, cancellationToken);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        document.RootElement.GetProperty("isAuthenticated").GetBoolean().Should().BeTrue(
            "the ticket lives in Redis and the Data Protection key ring survived on disk, so the restarted process must still authenticate the same cookie");
        document.RootElement.GetProperty("sub").GetString().Should().NotBeNullOrEmpty();
    }
}
