using System.Net;
using System.Text.Json;
using Decisya.Bff.Session;
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
}
