using System.Text;
using Decisya.Bff.Session;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Decisya.Bff.Tests;

/// <summary>
/// G5 (issue #18 traceability): Story 2's second scenario. Reads the raw bytes
/// <see cref="RedisTicketStore"/> wrote for the ticket key directly off the Redis
/// container — bypassing the protector entirely — and confirms the access and ID token
/// values are not sitting there in plaintext.
/// </summary>
[Trait("Category", "Integration")]
public class TicketProtectionTests
{
    private readonly KeycloakBffFixture _keycloakFixture;
    private readonly RedisFixture _redisFixture;

    public TicketProtectionTests(KeycloakBffFixture keycloakFixture, RedisFixture redisFixture)
    {
        _keycloakFixture = keycloakFixture;
        _redisFixture = redisFixture;
    }

    [Fact]
    public async Task The_stored_ticket_is_protected_not_plaintext()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-bob", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);
        result.BffJar.Cookies.TryGetValue("__Host-decisya-session", out var rawSessionCookie).Should().BeTrue();
        var sessionKey = SessionKeyExtractor.Extract(factory.Services, rawSessionCookie!);
        sessionKey.Should().NotBeNull();

        var ticketStore = factory.Services.GetRequiredService<RedisTicketStore>();
        var ticket = await ticketStore.RetrieveAsync(sessionKey!);
        ticket.Should().NotBeNull();

        var accessToken = ticket!.Properties.GetTokenValue("access_token");
        var idToken = ticket.Properties.GetTokenValue("id_token");
        (!string.IsNullOrEmpty(accessToken) || !string.IsNullOrEmpty(idToken)).Should().BeTrue(
            "SaveTokens should have put at least an access or an ID token on the ticket");

        var connectionMultiplexer = factory.Services.GetRequiredService<IConnectionMultiplexer>();
        var database = connectionMultiplexer.GetDatabase();
        var rawValue = await database.StringGetAsync($"decisya:bff:ticket:{sessionKey}");
        rawValue.HasValue.Should().BeTrue("the ticket entry should exist in Redis under its own key");

        // Latin1 is a lossless byte<->char mapping (not a claim the bytes are text); this
        // only needs to prove the ASCII-safe token values above are absent as a substring.
        var rawAsText = Encoding.Latin1.GetString((byte[])rawValue!);

        if (!string.IsNullOrEmpty(accessToken))
        {
            rawAsText.Should().NotContain(accessToken, "the access token must not sit in Redis in plaintext");
        }

        if (!string.IsNullOrEmpty(idToken))
        {
            rawAsText.Should().NotContain(idToken, "the ID token must not sit in Redis in plaintext");
        }
    }
}
