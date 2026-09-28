using System.Net;
using System.Text.Json;
using Decisya.Bff.Session;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Decisya.Bff.Tests;

/// <summary>
/// G5 (issue #18 traceability): the two clauses of Story 6 no other class covers directly —
/// scenario 1's "the corresponding ticket is deleted from the Redis-backed ticket store" and
/// scenario 3, "a cookie from before sign-out no longer works". <see cref="BffLoginFlowTests"/>
/// already covers the cleared cookie and the redirect's shape; <see cref="TokenLeakScanTests"/>
/// already covers the logout response carrying no token value.
/// </summary>
[Trait("Category", "Integration")]
public class LogoutTests
{
    private readonly KeycloakBffFixture _keycloakFixture;
    private readonly RedisFixture _redisFixture;

    public LogoutTests(KeycloakBffFixture keycloakFixture, RedisFixture redisFixture)
    {
        _keycloakFixture = keycloakFixture;
        _redisFixture = redisFixture;
    }

    [Fact]
    public async Task Signing_out_deletes_the_ticket_from_Redis_and_the_old_cookie_no_longer_works()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);
        result.BffJar.Cookies.TryGetValue("__Host-decisya-session", out var sessionCookie).Should().BeTrue();
        var sessionKey = SessionKeyExtractor.Extract(factory.Services, sessionCookie!);
        sessionKey.Should().NotBeNull();

        var ticketStore = factory.Services.GetRequiredService<RedisTicketStore>();
        (await ticketStore.RetrieveAsync(sessionKey!)).Should().NotBeNull("the ticket should exist before sign-out");

        using var meResponse = await result.BffClient.GetAsync("/bff/me", cancellationToken);
        meResponse.EnsureSuccessStatusCode();
        result.BffJar.Cookies.TryGetValue("__Host-decisya-xsrf", out var xsrfToken).Should().BeTrue();

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/bff/logout");
        logoutRequest.Headers.Add("X-XSRF-TOKEN", xsrfToken);
        using var logoutResponse = await result.BffClient.SendAsync(logoutRequest, cancellationToken);
        logoutResponse.StatusCode.Should().Be(HttpStatusCode.Found, "sign-out should redirect towards Keycloak's end-session endpoint");

        (await ticketStore.RetrieveAsync(sessionKey!)).Should().BeNull("sign-out should have deleted the ticket from Redis");

        using var rawClient = LoginFlowHarness.CreateRawBffClient(factory);
        using var meAfterLogoutRequest = new HttpRequestMessage(HttpMethod.Get, "/bff/me");
        meAfterLogoutRequest.Headers.TryAddWithoutValidation("Cookie", $"__Host-decisya-session={sessionCookie}");
        using var meAfterLogoutResponse = await rawClient.SendAsync(meAfterLogoutRequest, cancellationToken);

        using var document = JsonDocument.Parse(await meAfterLogoutResponse.Content.ReadAsStringAsync(cancellationToken));
        document.RootElement.GetProperty("isAuthenticated").GetBoolean().Should().BeFalse(
            "the cookie the browser held before sign-out must not authenticate afterwards");
    }

    [Fact]
    public async Task Logout_ends_the_session_at_Keycloak_so_the_pre_logout_refresh_token_no_longer_works()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);
        var countingHandler = new CountingBackchannelHandler(new HttpClientHandler());
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, backchannelHttpHandler: countingHandler);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);
        result.BffJar.Cookies.TryGetValue("__Host-decisya-session", out var sessionCookie).Should().BeTrue();
        var sessionKey = SessionKeyExtractor.Extract(factory.Services, sessionCookie!)!;

        var ticketStore = factory.Services.GetRequiredService<RedisTicketStore>();
        var preLogoutRefreshToken = (await ticketStore.RetrieveAsync(sessionKey))!.Properties.GetTokenValue("refresh_token")!;

        using (var meResponse = await result.BffClient.GetAsync("/bff/me", cancellationToken))
        {
            meResponse.EnsureSuccessStatusCode();
        }
        result.BffJar.Cookies.TryGetValue("__Host-decisya-xsrf", out var xsrfToken).Should().BeTrue();

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/bff/logout");
        logoutRequest.Headers.Add("X-XSRF-TOKEN", xsrfToken);
        using var logoutResponse = await result.BffClient.SendAsync(logoutRequest, cancellationToken);
        logoutResponse.StatusCode.Should().Be(HttpStatusCode.Found);

        countingHandler.EndSessionCallCount.Should().Be(1, "B-2: the BFF calls Keycloak's end-session endpoint using the held refresh token");

        // The end-session call ended the whole Keycloak SSO session (T-12's residual), so the
        // pre-logout refresh token can no longer mint a new access token either.
        using var rawKeycloakClient = new HttpClient { BaseAddress = new Uri(_keycloakFixture.BaseAddress) };
        using var refreshForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = preLogoutRefreshToken,
            ["client_id"] = "decisya-bff",
            ["client_secret"] = _keycloakFixture.ClientSecret,
        });
        using var refreshResponse = await rawKeycloakClient.PostAsync(
            "/realms/decisya/protocol/openid-connect/token", refreshForm, cancellationToken);

        refreshResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var refreshBody = await refreshResponse.Content.ReadAsStringAsync(cancellationToken);
        refreshBody.Should().Contain("invalid_grant");
    }

    [Fact]
    public async Task Logout_still_completes_locally_even_when_Keycloaks_revocation_call_fails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);
        var countingHandler = new CountingBackchannelHandler(new HttpClientHandler()) { ThrowOnEndSession = true };
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, backchannelHttpHandler: countingHandler);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);
        result.BffJar.Cookies.TryGetValue("__Host-decisya-session", out var sessionCookie).Should().BeTrue();
        var sessionKey = SessionKeyExtractor.Extract(factory.Services, sessionCookie!)!;
        var ticketStore = factory.Services.GetRequiredService<RedisTicketStore>();

        using (var meResponse = await result.BffClient.GetAsync("/bff/me", cancellationToken))
        {
            meResponse.EnsureSuccessStatusCode();
        }
        result.BffJar.Cookies.TryGetValue("__Host-decisya-xsrf", out var xsrfToken).Should().BeTrue();

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/bff/logout");
        logoutRequest.Headers.Add("X-XSRF-TOKEN", xsrfToken);
        using var logoutResponse = await result.BffClient.SendAsync(logoutRequest, cancellationToken);

        // G1 decision 2 (fail-open): a Keycloak-side failure never surfaces as an error to the
        // browser; the local logout (cookie cleared, ticket deleted, redirect) is unchanged.
        logoutResponse.StatusCode.Should().Be(HttpStatusCode.Found);
        countingHandler.EndSessionCallCount.Should().Be(1);
        (await ticketStore.RetrieveAsync(sessionKey)).Should().BeNull("the local logout must still complete");
    }
}
