using System.Net;
using System.Text.Json;

namespace Decisya.Bff.Tests;

/// <summary>
/// G4-18-02 (T-04; Story 4, Story 6 scenario 4): <c>POST /bff/logout</c> requires the
/// double-submit antiforgery pair, bound to the caller's own session.
/// </summary>
[Trait("Category", "Integration")]
public class AntiforgeryTests
{
    private readonly KeycloakBffFixture _keycloakFixture;
    private readonly RedisFixture _redisFixture;

    public AntiforgeryTests(KeycloakBffFixture keycloakFixture, RedisFixture redisFixture)
    {
        _keycloakFixture = keycloakFixture;
        _redisFixture = redisFixture;
    }

    [Fact]
    public async Task A_state_changing_request_with_no_antiforgery_header_is_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);
        await FetchAntiforgeryPairAsync(result.BffClient, cancellationToken);

        using var logoutResponse = await result.BffClient.PostAsync("/bff/logout", content: null, cancellationToken);

        logoutResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertSessionStillValidAsync(result.BffClient, cancellationToken);
    }

    [Fact]
    public async Task A_state_changing_request_with_a_mismatched_header_is_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);
        await FetchAntiforgeryPairAsync(result.BffClient, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/logout");
        request.Headers.Add("X-XSRF-TOKEN", Canaries.Unique("mismatched-xsrf-token"));
        using var logoutResponse = await result.BffClient.SendAsync(request, cancellationToken);

        logoutResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertSessionStillValidAsync(result.BffClient, cancellationToken);
    }

    [Fact]
    public async Task Another_users_antiforgery_pair_presented_with_this_session_is_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture);

        var aliceResult = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);
        aliceResult.BffJar.Cookies.TryGetValue("__Host-decisya-session", out var aliceSession).Should().BeTrue();

        var bobResult = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-bob", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);
        await FetchAntiforgeryPairAsync(bobResult.BffClient, cancellationToken);
        bobResult.BffJar.Cookies.TryGetValue("__Host-decisya-af", out var bobAf).Should().BeTrue();
        bobResult.BffJar.Cookies.TryGetValue("__Host-decisya-xsrf", out var bobXsrf).Should().BeTrue();

        using var rawClient = LoginFlowHarness.CreateRawBffClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/logout");
        request.Headers.TryAddWithoutValidation(
            "Cookie", $"__Host-decisya-session={aliceSession}; __Host-decisya-af={bobAf}");
        request.Headers.Add("X-XSRF-TOKEN", bobXsrf);
        using var logoutResponse = await rawClient.SendAsync(request, cancellationToken);

        logoutResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertSessionStillValidAsync(aliceResult.BffClient, cancellationToken);
    }

    [Fact]
    public async Task A_pair_issued_while_anonymous_is_rejected_once_presented_after_login()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture);

        var bffJar = new OriginCookieJar();
        var bffClient = new HttpClient(new OriginCookieHandler(bffJar, factory.Server.CreateHandler()))
        {
            BaseAddress = new Uri("https://localhost:7200"),
        };

        // The anonymous pair: never overwritten below, because LogInWithClientAsync never
        // calls /bff/me itself.
        await FetchAntiforgeryPairAsync(bffClient, cancellationToken);
        bffJar.Cookies.TryGetValue("__Host-decisya-af", out var anonymousAf).Should().BeTrue();
        bffJar.Cookies.TryGetValue("__Host-decisya-xsrf", out var anonymousXsrf).Should().BeTrue();

        await LoginFlowHarness.LogInWithClientAsync(
            bffClient, bffJar, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);

        bffJar.Cookies["__Host-decisya-af"].Should().Be(anonymousAf, "the harness should not have re-issued the pair");

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/bff/logout");
        logoutRequest.Headers.Add("X-XSRF-TOKEN", anonymousXsrf);
        using var logoutResponse = await bffClient.SendAsync(logoutRequest, cancellationToken);

        logoutResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertSessionStillValidAsync(bffClient, cancellationToken);
    }

    [Fact]
    public async Task A_valid_antiforgery_pair_is_accepted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);
        await FetchAntiforgeryPairAsync(result.BffClient, cancellationToken);
        result.BffJar.Cookies.TryGetValue("__Host-decisya-xsrf", out var xsrfToken).Should().BeTrue();

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/bff/logout");
        logoutRequest.Headers.Add("X-XSRF-TOKEN", xsrfToken);
        using var logoutResponse = await result.BffClient.SendAsync(logoutRequest, cancellationToken);

        logoutResponse.StatusCode.Should().Be(HttpStatusCode.Found, "a valid pair should let the request through to the handler");
    }

    [Fact]
    public async Task A_safe_GET_request_needs_no_antiforgery_header()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);

        using var meResponse = await result.BffClient.GetAsync("/bff/me", cancellationToken);

        meResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task FetchAntiforgeryPairAsync(HttpClient bffClient, CancellationToken cancellationToken)
    {
        using var response = await bffClient.GetAsync("/bff/me", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task AssertSessionStillValidAsync(HttpClient bffClient, CancellationToken cancellationToken)
    {
        using var response = await bffClient.GetAsync("/bff/me", cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        document.RootElement.GetProperty("isAuthenticated").GetBoolean().Should().BeTrue(
            "a rejected antiforgery attempt must not have signed the caller out");
    }
}
