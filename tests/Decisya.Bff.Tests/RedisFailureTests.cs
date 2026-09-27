using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Decisya.Bff.Tests;

/// <summary>
/// S-3 (T-07): a corrupted ticket entry degrades exactly like a Redis failure — <c>/bff/me</c>
/// reports <c>isAuthenticated: false</c>, never a 500, and the session is not served from
/// anywhere else.
/// </summary>
[Trait("Category", "Integration")]
public class RedisFailureTests
{
    private readonly KeycloakBffFixture _keycloakFixture;
    private readonly RedisFixture _redisFixture;

    public RedisFailureTests(KeycloakBffFixture keycloakFixture, RedisFixture redisFixture)
    {
        _keycloakFixture = keycloakFixture;
        _redisFixture = redisFixture;
    }

    [Fact]
    public async Task A_corrupted_ticket_entry_reports_unauthenticated_not_a_server_error()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);
        result.BffJar.Cookies.TryGetValue("__Host-decisya-session", out var rawSessionCookie).Should().BeTrue();
        var sessionKey = SessionKeyExtractor.Extract(factory.Services, rawSessionCookie!);
        sessionKey.Should().NotBeNull();

        // Overwrites the ticket's Redis entry with garbage bytes that cannot unprotect.
        var connectionMultiplexer = factory.Services.GetRequiredService<IConnectionMultiplexer>();
        var database = connectionMultiplexer.GetDatabase();
        await database.StringSetAsync($"decisya:bff:ticket:{sessionKey}", "not-a-protected-ticket"u8.ToArray());

        using var meResponse = await result.BffClient.GetAsync("/bff/me", cancellationToken);

        meResponse.StatusCode.Should().Be(HttpStatusCode.OK, "a corrupted ticket must never surface as a 500");
        using var document = JsonDocument.Parse(await meResponse.Content.ReadAsStringAsync(cancellationToken));
        document.RootElement.GetProperty("isAuthenticated").GetBoolean().Should().BeFalse();
    }
}
