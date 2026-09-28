using System.Globalization;
using System.Net;
using Decisya.Bff.Session;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Testing;

namespace Decisya.Bff.Tests;

/// <summary>
/// #19 G4-19-04, Stories 2-4: the refresh state machine. Expiry is controlled by a
/// <see cref="FakeClock"/> started at the real instant (G2's test-harness note): the OIDC
/// handler still writes <c>expires_at</c> from real time at login, and the fake clock is then
/// moved relative to that value.
/// </summary>
[Trait("Category", "Integration")]
public class TokenRefreshTests
{
    private readonly KeycloakBffFixture _keycloakFixture;
    private readonly RedisFixture _redisFixture;

    public TokenRefreshTests(KeycloakBffFixture keycloakFixture, RedisFixture redisFixture)
    {
        _keycloakFixture = keycloakFixture;
        _redisFixture = redisFixture;
    }

    [Fact]
    public async Task Expired_access_token_is_refreshed_before_the_call_is_forwarded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);

        await using var apiDouble = await ApiDouble.StartAsync(cancellationToken);
        var fakeClock = new FakeClock(NodaTime.SystemClock.Instance.GetCurrentInstant());
        var countingHandler = new CountingBackchannelHandler(new HttpClientHandler());

        using var factory = BffFactoryFactory.Create(
            _keycloakFixture, _redisFixture, apiAddress: apiDouble.Address, clock: fakeClock, backchannelHttpHandler: countingHandler);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);
        result.BffJar.Cookies.TryGetValue("__Host-decisya-session", out var sessionCookie).Should().BeTrue();
        var sessionKey = SessionKeyExtractor.Extract(factory.Services, sessionCookie!)!;

        var ticketStore = factory.Services.GetRequiredService<RedisTicketStore>();
        var ticketBeforeRefresh = await ticketStore.RetrieveAsync(sessionKey);
        var oldAccessToken = ticketBeforeRefresh!.Properties.GetTokenValue("access_token");
        var oldRefreshToken = ticketBeforeRefresh.Properties.GetTokenValue("refresh_token");
        var expiresAt = ParseExpiresAt(ticketBeforeRefresh);

        // Already expired.
        fakeClock.Reset(expiresAt.Plus(Duration.FromSeconds(5)));

        using var apiResponse = await result.BffClient.GetAsync("/api/x", cancellationToken);

        apiResponse.StatusCode.Should().Be(HttpStatusCode.OK, "the Done-when: the call succeeds after a transparent refresh");
        countingHandler.RefreshCallCount.Should().Be(1);

        apiDouble.Requests.Should().HaveCount(1);
        var forwardedAuthorization = apiDouble.Requests.Single().Headers["Authorization"].Single();
        forwardedAuthorization.Should().NotBe($"Bearer {oldAccessToken}", "the forwarded token must be the new one, not the expired one");

        var ticketAfterRefresh = await ticketStore.RetrieveAsync(sessionKey);
        ticketAfterRefresh!.Properties.GetTokenValue("access_token").Should().NotBe(oldAccessToken);
        ticketAfterRefresh.Properties.GetTokenValue("refresh_token").Should().NotBe(oldRefreshToken,
            "Keycloak's strict rotation always issues a new refresh token too");
        forwardedAuthorization.Should().Be($"Bearer {ticketAfterRefresh.Properties.GetTokenValue("access_token")}");
    }

    [Fact]
    public async Task A_token_nearing_expiry_is_refreshed_proactively_without_waiting_for_outright_expiry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);

        await using var apiDouble = await ApiDouble.StartAsync(cancellationToken);
        var fakeClock = new FakeClock(NodaTime.SystemClock.Instance.GetCurrentInstant());
        var countingHandler = new CountingBackchannelHandler(new HttpClientHandler());

        using var factory = BffFactoryFactory.Create(
            _keycloakFixture, _redisFixture, apiAddress: apiDouble.Address, clock: fakeClock, backchannelHttpHandler: countingHandler);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);
        result.BffJar.Cookies.TryGetValue("__Host-decisya-session", out var sessionCookie).Should().BeTrue();
        var sessionKey = SessionKeyExtractor.Extract(factory.Services, sessionCookie!)!;
        var ticketStore = factory.Services.GetRequiredService<RedisTicketStore>();
        var expiresAt = ParseExpiresAt((await ticketStore.RetrieveAsync(sessionKey))!);

        // NFR-24: 29 s remaining, below the 30 s lead time.
        fakeClock.Reset(expiresAt.Minus(Duration.FromSeconds(29)));

        using var apiResponse = await result.BffClient.GetAsync("/api/x", cancellationToken);

        apiResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        countingHandler.RefreshCallCount.Should().Be(1, "29 s remaining is below the 30 s lead time (NFR-24)");
    }

    [Fact]
    public async Task A_token_with_plenty_of_remaining_lifetime_is_forwarded_unchanged_with_no_refresh_call()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);

        await using var apiDouble = await ApiDouble.StartAsync(cancellationToken);
        var fakeClock = new FakeClock(NodaTime.SystemClock.Instance.GetCurrentInstant());
        var countingHandler = new CountingBackchannelHandler(new HttpClientHandler());

        using var factory = BffFactoryFactory.Create(
            _keycloakFixture, _redisFixture, apiAddress: apiDouble.Address, clock: fakeClock, backchannelHttpHandler: countingHandler);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);
        result.BffJar.Cookies.TryGetValue("__Host-decisya-session", out var sessionCookie).Should().BeTrue();
        var sessionKey = SessionKeyExtractor.Extract(factory.Services, sessionCookie!)!;
        var ticketStore = factory.Services.GetRequiredService<RedisTicketStore>();
        var ticketBefore = await ticketStore.RetrieveAsync(sessionKey);
        var oldAccessToken = ticketBefore!.Properties.GetTokenValue("access_token");
        var expiresAt = ParseExpiresAt(ticketBefore);

        // NFR-24: 31 s remaining, above the 30 s lead time.
        fakeClock.Reset(expiresAt.Minus(Duration.FromSeconds(31)));

        using var apiResponse = await result.BffClient.GetAsync("/api/x", cancellationToken);

        apiResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        countingHandler.RefreshCallCount.Should().Be(0, "31 s remaining is above the 30 s lead time (NFR-24); no call to Keycloak's token endpoint");
        apiDouble.Requests.Single().Headers["Authorization"].Single().Should().Be($"Bearer {oldAccessToken}");
    }

    [Fact]
    public async Task Ten_concurrent_requests_against_an_expired_token_share_exactly_one_refresh()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);

        await using var apiDouble = await ApiDouble.StartAsync(cancellationToken);
        var fakeClock = new FakeClock(NodaTime.SystemClock.Instance.GetCurrentInstant());
        var countingHandler = new CountingBackchannelHandler(new HttpClientHandler());

        using var factory = BffFactoryFactory.Create(
            _keycloakFixture, _redisFixture, apiAddress: apiDouble.Address, clock: fakeClock, backchannelHttpHandler: countingHandler);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);
        result.BffJar.Cookies.TryGetValue("__Host-decisya-session", out var sessionCookie).Should().BeTrue();
        var sessionKey = SessionKeyExtractor.Extract(factory.Services, sessionCookie!)!;
        var ticketStore = factory.Services.GetRequiredService<RedisTicketStore>();
        var expiresAt = ParseExpiresAt((await ticketStore.RetrieveAsync(sessionKey))!);

        fakeClock.Reset(expiresAt.Plus(Duration.FromSeconds(5)));

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => result.BffClient.GetAsync("/api/x", cancellationToken)));

        try
        {
            responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);
            countingHandler.RefreshCallCount.Should().Be(1, "NFR-25: exactly one refresh call per session even under 10 concurrent requests");

            apiDouble.Requests.Should().HaveCount(10);
            apiDouble.Requests.Select(request => request.Headers["Authorization"].Single()).Distinct().Should().ContainSingle(
                "all 10 forwarded requests must carry the same new access token");
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task A_refresh_Keycloak_rejects_ends_the_session_at_once_and_the_next_call_gets_401()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);

        await using var apiDouble = await ApiDouble.StartAsync(cancellationToken);
        var fakeClock = new FakeClock(NodaTime.SystemClock.Instance.GetCurrentInstant());

        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, apiAddress: apiDouble.Address, clock: fakeClock);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);
        result.BffJar.Cookies.TryGetValue("__Host-decisya-session", out var sessionCookie).Should().BeTrue();
        var sessionKey = SessionKeyExtractor.Extract(factory.Services, sessionCookie!)!;
        var ticketStore = factory.Services.GetRequiredService<RedisTicketStore>();
        var ticket = await ticketStore.RetrieveAsync(sessionKey);
        var expiresAt = ParseExpiresAt(ticket!);

        // Story 4: "whose refresh token Keycloak will reject" — a corrupted refresh token
        // gives the same invalid_grant outcome as a revoked or expired one, without needing
        // the admin API to end a Keycloak session.
        ticket!.Properties.UpdateTokenValue("refresh_token", "corrupted-" + Guid.NewGuid().ToString("N"));
        await ticketStore.RenewAsync(sessionKey, ticket);

        fakeClock.Reset(expiresAt.Plus(Duration.FromSeconds(5)));

        using var firstResponse = await result.BffClient.GetAsync("/api/x", cancellationToken);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        apiDouble.Requests.Should().BeEmpty("no request is forwarded when the refresh is rejected");
        (await ticketStore.RetrieveAsync(sessionKey)).Should().BeNull("B-1: the ticket is deleted at once");

        using var secondResponse = await result.BffClient.GetAsync("/api/x", cancellationToken);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        apiDouble.Requests.Should().BeEmpty("the next call must not forward a stale or invalid token either");
    }

    [Fact]
    public async Task A_network_failure_reaching_Keycloak_during_refresh_returns_503_and_keeps_the_session()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);

        await using var apiDouble = await ApiDouble.StartAsync(cancellationToken);
        var fakeClock = new FakeClock(NodaTime.SystemClock.Instance.GetCurrentInstant());
        var countingHandler = new CountingBackchannelHandler(new HttpClientHandler()) { ThrowOnRefresh = true };

        using var factory = BffFactoryFactory.Create(
            _keycloakFixture, _redisFixture, apiAddress: apiDouble.Address, clock: fakeClock, backchannelHttpHandler: countingHandler);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);
        result.BffJar.Cookies.TryGetValue("__Host-decisya-session", out var sessionCookie).Should().BeTrue();
        var sessionKey = SessionKeyExtractor.Extract(factory.Services, sessionCookie!)!;
        var ticketStore = factory.Services.GetRequiredService<RedisTicketStore>();
        var ticketBefore = await ticketStore.RetrieveAsync(sessionKey);
        var oldAccessToken = ticketBefore!.Properties.GetTokenValue("access_token");
        var expiresAt = ParseExpiresAt(ticketBefore);

        fakeClock.Reset(expiresAt.Plus(Duration.FromSeconds(5)));

        using var response = await result.BffClient.GetAsync("/api/x", cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        apiDouble.Requests.Should().BeEmpty();
        countingHandler.RefreshCallCount.Should().Be(1, "no retry under strict refresh-token rotation");

        var ticketAfter = await ticketStore.RetrieveAsync(sessionKey);
        ticketAfter.Should().NotBeNull("D6: the session is kept on a non-invalid_grant failure");
        ticketAfter!.Properties.GetTokenValue("access_token").Should().Be(oldAccessToken);
    }

    private static Instant ParseExpiresAt(AuthenticationTicket ticket)
    {
        var raw = ticket.Properties.GetTokenValue("expires_at")!;
        var expiresAt = DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return Instant.FromDateTimeOffset(expiresAt);
    }
}
