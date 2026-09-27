using Decisya.Bff.Session;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Decisya.Bff.Tests;

/// <summary>
/// G4-18-01's second red test: every response across the whole login-to-logout flow is
/// scanned for the access, ID and refresh token values read back from Redis through
/// <c>RedisTicketStore</c>'s own protector — never a hard-coded or guessed token shape.
/// </summary>
[Trait("Category", "Integration")]
public class TokenLeakScanTests
{
    private readonly KeycloakBffFixture _keycloakFixture;
    private readonly RedisFixture _redisFixture;

    public TokenLeakScanTests(KeycloakBffFixture keycloakFixture, RedisFixture redisFixture)
    {
        _keycloakFixture = keycloakFixture;
        _redisFixture = redisFixture;
    }

    [Fact]
    public async Task No_token_value_appears_in_any_response_across_the_flow()
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
        ticket.Should().NotBeNull("the ticket store should hold the ticket the callback just wrote");

        var accessToken = ticket!.Properties.GetTokenValue("access_token");
        var idToken = ticket.Properties.GetTokenValue("id_token");
        var refreshToken = ticket.Properties.GetTokenValue("refresh_token");

        var valuesToScan = new List<string>();
        AddIfPresent(valuesToScan, accessToken);
        AddIfPresent(valuesToScan, idToken);
        AddIfPresent(valuesToScan, refreshToken);
        valuesToScan.Should().NotBeEmpty("SaveTokens should have put at least an access and an ID token on the ticket");

        // Scan each value both raw and as its JWT payload segment (G4-18-01).
        var payloadSegments = valuesToScan
            .Where(value => value.Count(c => c == '.') >= 2)
            .Select(value => value.Split('.')[1])
            .Where(segment => segment.Length > 0)
            .ToList();
        valuesToScan.AddRange(payloadSegments);

        // /bff/me and /bff/logout too, not only the login hops LogInAsync itself captured.
        var allExchanges = result.Exchanges.ToList();
        using var meResponse = await result.BffClient.GetAsync("/bff/me", cancellationToken);
        allExchanges.Add(await LoginFlowHarness.CaptureAsync("bff-me", meResponse, cancellationToken));

        result.BffJar.Cookies.TryGetValue("__Host-decisya-xsrf", out var xsrfToken);
        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/bff/logout");
        logoutRequest.Headers.Add("X-XSRF-TOKEN", xsrfToken);
        using var logoutResponse = await result.BffClient.SendAsync(logoutRequest, cancellationToken);
        allExchanges.Add(await LoginFlowHarness.CaptureAsync("bff-logout", logoutResponse, cancellationToken));

        foreach (var exchange in allExchanges)
        {
            foreach (var value in valuesToScan)
            {
                exchange.Body.Should().NotContain(value, $"{exchange.Step}'s body must carry no token value");
                exchange.StatusCode.ToString().Should().NotContain(value);

                foreach (var (headerName, headerValues) in exchange.Headers)
                {
                    foreach (var headerValue in headerValues)
                    {
                        headerValue.Should().NotContain(
                            value, $"{exchange.Step}'s '{headerName}' header must carry no token value");
                    }
                }
            }
        }
    }

    private static void AddIfPresent(List<string> values, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            values.Add(value);
        }
    }
}
