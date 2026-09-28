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

    /// <summary>
    /// #19 G4-19-02: extends the scan to every <c>/api</c> flow — forwarded 200, refreshed,
    /// 401 after B-1, 403 (antiforgery), and 503 (refresh unavailable) — using the same
    /// technique (tokens read back through the store's own protector, scanned raw and as the
    /// JWT payload segment).
    /// </summary>
    [Fact]
    public async Task No_token_value_appears_in_any_api_response_across_forward_refresh_401_403_or_503()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);

        await using var apiDouble = await ApiDouble.StartAsync(cancellationToken);
        var fakeClock = new NodaTime.Testing.FakeClock(NodaTime.SystemClock.Instance.GetCurrentInstant());
        var countingHandler = new CountingBackchannelHandler(new HttpClientHandler());
        using var factory = BffFactoryFactory.Create(
            _keycloakFixture, _redisFixture, apiAddress: apiDouble.Address, clock: fakeClock, backchannelHttpHandler: countingHandler);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-bob", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);
        result.BffJar.Cookies.TryGetValue("__Host-decisya-session", out var rawSessionCookie).Should().BeTrue();
        var sessionKey = SessionKeyExtractor.Extract(factory.Services, rawSessionCookie!)!;
        var ticketStore = factory.Services.GetRequiredService<RedisTicketStore>();

        var ticket = await ticketStore.RetrieveAsync(sessionKey);
        var accessToken = ticket!.Properties.GetTokenValue("access_token")!;
        var refreshToken = ticket.Properties.GetTokenValue("refresh_token")!;
        var expiresAtRaw = ticket.Properties.GetTokenValue("expires_at")!;
        var expiresAt = DateTimeOffset.Parse(expiresAtRaw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);

        var allExchanges = new List<CapturedExchange>();

        // 200, forwarded.
        using (var response = await result.BffClient.GetAsync("/api/x", cancellationToken))
        {
            allExchanges.Add(await LoginFlowHarness.CaptureAsync("api-forwarded", response, cancellationToken));
        }

        // Refreshed: advance past expiry.
        fakeClock.Reset(NodaTime.Instant.FromDateTimeOffset(expiresAt) + NodaTime.Duration.FromSeconds(5));
        var refreshedAccessToken = accessToken;
        var refreshedRefreshToken = refreshToken;
        using (var response = await result.BffClient.GetAsync("/api/x", cancellationToken))
        {
            allExchanges.Add(await LoginFlowHarness.CaptureAsync("api-refreshed", response, cancellationToken));
        }

        var refreshedTicket = await ticketStore.RetrieveAsync(sessionKey);
        if (refreshedTicket is not null)
        {
            refreshedAccessToken = refreshedTicket.Properties.GetTokenValue("access_token") ?? accessToken;
            refreshedRefreshToken = refreshedTicket.Properties.GetTokenValue("refresh_token") ?? refreshToken;
        }

        // 403: no antiforgery header on a mutating call.
        using (var response = await result.BffClient.PostAsync("/api/x", content: null, cancellationToken))
        {
            allExchanges.Add(await LoginFlowHarness.CaptureAsync("api-403", response, cancellationToken));
        }

        // 401 (B-1): corrupt the refresh token, then force another refresh attempt.
        var preInvalidGrantTicket = await ticketStore.RetrieveAsync(sessionKey);
        preInvalidGrantTicket!.Properties.UpdateTokenValue("refresh_token", "corrupted-" + Guid.NewGuid().ToString("N"));
        await ticketStore.RenewAsync(sessionKey, preInvalidGrantTicket);
        fakeClock.Reset(fakeClock.GetCurrentInstant() + NodaTime.Duration.FromSeconds(400));
        using (var response = await result.BffClient.GetAsync("/api/x", cancellationToken))
        {
            allExchanges.Add(await LoginFlowHarness.CaptureAsync("api-401-invalid-grant", response, cancellationToken));
        }

        var valuesToScan = new List<string> { accessToken, refreshToken, refreshedAccessToken, refreshedRefreshToken };
        var payloadSegments = valuesToScan
            .Distinct()
            .Where(value => value.Count(c => c == '.') >= 2)
            .Select(value => value.Split('.')[1])
            .Where(segment => segment.Length > 0)
            .ToList();
        valuesToScan.AddRange(payloadSegments);
        valuesToScan = valuesToScan.Distinct().ToList();

        foreach (var exchange in allExchanges)
        {
            foreach (var value in valuesToScan)
            {
                exchange.Body.Should().NotContain(value, $"{exchange.Step}'s body must carry no token value");

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
