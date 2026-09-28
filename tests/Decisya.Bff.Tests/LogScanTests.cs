using Decisya.Bff.Session;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Testing;

namespace Decisya.Bff.Tests;

/// <summary>
/// #19 G3 MUST G4-19-05: the automated log scan G4 left for G5. Every log record produced across
/// login, a forwarded <c>/api</c> call, a forced refresh, a refresh Keycloak rejects
/// (<c>invalid_grant</c>), and sign-out (including the server-side end-session call) is captured
/// through an in-memory <c>ILoggerProvider</c> (<see cref="CapturingLoggerProvider"/>)
/// with every category forced to <see cref="Microsoft.Extensions.Logging.LogLevel.Debug"/>. No
/// record's message, structured state values or exception text may contain the access, refresh
/// or ID token, the client secret, or the session key — the same technique
/// <see cref="TokenLeakScanTests"/> uses for HTTP responses, applied to the log output
/// <c>Decisya.Bff.Session.BffLog</c>'s own message templates were designed never to carry
/// (CLAUDE.md; <c>BffLog.cs</c>'s own header comment), proving it rather than accepting it by
/// inspection.
/// </summary>
[Trait("Category", "Integration")]
public class LogScanTests
{
    private readonly KeycloakBffFixture _keycloakFixture;
    private readonly RedisFixture _redisFixture;

    public LogScanTests(KeycloakBffFixture keycloakFixture, RedisFixture redisFixture)
    {
        _keycloakFixture = keycloakFixture;
        _redisFixture = redisFixture;
    }

    [Fact]
    public async Task No_log_record_across_login_forward_refresh_refresh_failure_or_logout_carries_a_token_the_client_secret_or_the_session_key()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);

        await using var apiDouble = await ApiDouble.StartAsync(cancellationToken);
        var fakeClock = new FakeClock(NodaTime.SystemClock.Instance.GetCurrentInstant());
        var countingHandler = new CountingBackchannelHandler(new HttpClientHandler());
        var loggerProvider = new CapturingLoggerProvider();

        using var factory = BffFactoryFactory.Create(
            _keycloakFixture,
            _redisFixture,
            apiAddress: apiDouble.Address,
            clock: fakeClock,
            backchannelHttpHandler: countingHandler,
            loggerProvider: loggerProvider);

        var ticketStore = factory.Services.GetRequiredService<RedisTicketStore>();
        var sensitiveValues = new List<string> { _keycloakFixture.ClientSecret };

        // Login (dev-alice), a forwarded /api call, a forced refresh, then a refresh Keycloak
        // rejects (invalid_grant), which ends the session (B-1).
        var aliceResult = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);
        aliceResult.BffJar.Cookies.TryGetValue("__Host-decisya-session", out var aliceSessionCookie).Should().BeTrue();
        var aliceSessionKey = SessionKeyExtractor.Extract(factory.Services, aliceSessionCookie!)!;
        sensitiveValues.Add(aliceSessionKey);

        var aliceTicket = await ticketStore.RetrieveAsync(aliceSessionKey);
        AddTokens(sensitiveValues, aliceTicket!);
        var expiresAt = ParseExpiresAt(aliceTicket!);

        using (var forwardedResponse = await aliceResult.BffClient.GetAsync("/api/x", cancellationToken))
        {
            forwardedResponse.EnsureSuccessStatusCode();
        }

        // Forced refresh: past expiry.
        fakeClock.Reset(expiresAt.Plus(Duration.FromSeconds(5)));
        using (var refreshedResponse = await aliceResult.BffClient.GetAsync("/api/x", cancellationToken))
        {
            refreshedResponse.EnsureSuccessStatusCode();
        }
        countingHandler.RefreshCallCount.Should().Be(1, "the second call should have forced exactly one refresh");

        var refreshedTicket = await ticketStore.RetrieveAsync(aliceSessionKey);
        AddTokens(sensitiveValues, refreshedTicket!);
        var refreshedExpiresAt = ParseExpiresAt(refreshedTicket!);

        // A refresh Keycloak rejects (invalid_grant, B-1): corrupt the refresh token, then force
        // another refresh attempt.
        refreshedTicket!.Properties.UpdateTokenValue("refresh_token", "corrupted-" + Guid.NewGuid().ToString("N"));
        await ticketStore.RenewAsync(aliceSessionKey, refreshedTicket);
        fakeClock.Reset(refreshedExpiresAt.Plus(Duration.FromSeconds(5)));
        using (var rejectedResponse = await aliceResult.BffClient.GetAsync("/api/x", cancellationToken))
        {
            rejectedResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
        }

        // Sign-out (dev-bob, a fresh session), including the server-side end-session call (B-2).
        var bobResult = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-bob", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);
        bobResult.BffJar.Cookies.TryGetValue("__Host-decisya-session", out var bobSessionCookie).Should().BeTrue();
        var bobSessionKey = SessionKeyExtractor.Extract(factory.Services, bobSessionCookie!)!;
        sensitiveValues.Add(bobSessionKey);

        var bobTicket = await ticketStore.RetrieveAsync(bobSessionKey);
        AddTokens(sensitiveValues, bobTicket!);

        using (var meResponse = await bobResult.BffClient.GetAsync("/bff/me", cancellationToken))
        {
            meResponse.EnsureSuccessStatusCode();
        }
        bobResult.BffJar.Cookies.TryGetValue("__Host-decisya-xsrf", out var xsrfToken).Should().BeTrue();

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/bff/logout");
        logoutRequest.Headers.Add("X-XSRF-TOKEN", xsrfToken);
        using (var logoutResponse = await bobResult.BffClient.SendAsync(logoutRequest, cancellationToken))
        {
            logoutResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Found);
        }
        countingHandler.EndSessionCallCount.Should().Be(1, "logout should have called Keycloak's end-session endpoint server-side");

        // Also scan the JWT payload segment of each token (the same technique TokenLeakScanTests
        // uses): a leak could echo just the decoded claims, not the raw encoded token.
        var payloadSegments = sensitiveValues
            .Distinct()
            .Where(value => value.Count(c => c == '.') >= 2)
            .Select(value => value.Split('.')[1])
            .Where(segment => segment.Length > 0)
            .ToList();
        sensitiveValues.AddRange(payloadSegments);
        sensitiveValues = sensitiveValues.Distinct().ToList();

        var records = loggerProvider.Records.ToList();
        records.Should().NotBeEmpty("forcing every category to Debug should have produced log records to scan");

        foreach (var record in records)
        {
            foreach (var value in sensitiveValues)
            {
                record.Contains(value).Should().BeFalse(
                    $"[{record.Category}] '{record.Message}' (state: '{record.StateText}') must not carry a token, the client secret or a session key");
            }
        }
    }

    private static void AddTokens(List<string> sensitiveValues, AuthenticationTicket ticket)
    {
        AddIfPresent(sensitiveValues, ticket.Properties.GetTokenValue("access_token"));
        AddIfPresent(sensitiveValues, ticket.Properties.GetTokenValue("id_token"));
        AddIfPresent(sensitiveValues, ticket.Properties.GetTokenValue("refresh_token"));
    }

    private static void AddIfPresent(List<string> values, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            values.Add(value);
        }
    }

    private static Instant ParseExpiresAt(AuthenticationTicket ticket)
    {
        var raw = ticket.Properties.GetTokenValue("expires_at")!;
        var expiresAt = DateTimeOffset.Parse(
            raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
        return Instant.FromDateTimeOffset(expiresAt);
    }
}
