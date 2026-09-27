using System.Net;
using System.Text.Json;
using Decisya.Bff.Session;
using Microsoft.Extensions.DependencyInjection;

namespace Decisya.Bff.Tests;

/// <summary>
/// G4-18-05 (T-05): a sign-in that arrives at <c>/signin-oidc</c> carrying a different
/// caller's still-valid session cookie must mint a fresh session key, not renew the old one
/// in place (dotnet/aspnetcore#22135). Session-key strings are read straight from
/// <see cref="OriginCookieJar"/> and never logged or printed by name.
/// </summary>
[Trait("Category", "Integration")]
public class SessionFixationTests
{
    private readonly KeycloakBffFixture _keycloakFixture;
    private readonly RedisFixture _redisFixture;

    public SessionFixationTests(KeycloakBffFixture keycloakFixture, RedisFixture redisFixture)
    {
        _keycloakFixture = keycloakFixture;
        _redisFixture = redisFixture;
    }

    [Fact]
    public async Task Sign_in_with_an_existing_session_cookie_issues_a_new_key()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture);

        var bobResult = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-bob", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);
        bobResult.BffJar.Cookies.TryGetValue("__Host-decisya-session", out var bobSessionCookie).Should().BeTrue();
        var bobSessionKey = SessionKeyExtractor.Extract(factory.Services, bobSessionCookie!);
        bobSessionKey.Should().NotBeNull();

        var ticketStore = factory.Services.GetRequiredService<RedisTicketStore>();
        (await ticketStore.RetrieveAsync(bobSessionKey!)).Should().NotBeNull("dev-bob's ticket should exist before the attempt");

        // Drive dev-alice's own challenge and Keycloak login normally, then attach dev-bob's
        // still-valid session cookie to the /signin-oidc callback only — never to the
        // challenge — matching the red test's exact setup.
        var aliceJar = new OriginCookieJar();
        var aliceBffClient = new HttpClient(new OriginCookieHandler(aliceJar, factory.Server.CreateHandler()))
        {
            BaseAddress = new Uri("https://localhost:7200"),
        };
        var keycloakJar = new OriginCookieJar();
        using var keycloakClient = new HttpClient(
            new OriginCookieHandler(keycloakJar, new HttpClientHandler { AllowAutoRedirect = false }))
        {
            BaseAddress = new Uri(_keycloakFixture.BaseAddress),
        };

        using var challengeResponse = await aliceBffClient.GetAsync("/bff/login?returnUrl=/dashboard", cancellationToken);
        var authorizeUrl = challengeResponse.Headers.Location!;

        var exchanges = new List<CapturedExchange>();
        var formAction = await LoginFlowHarness.FollowToLoginFormAsync(keycloakClient, authorizeUrl, exchanges, cancellationToken);

        using var loginFormResponse = await keycloakClient.PostAsync(
            formAction,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "dev-alice",
                ["password"] = _keycloakFixture.DevUserPassword,
            }),
            cancellationToken);
        var callbackUrl = loginFormResponse.Headers.Location!;

        using var callbackRequest = new HttpRequestMessage(HttpMethod.Get, callbackUrl);
        var plantedCookieHeader = string.Join("; ", aliceJar.Cookies.Select(pair => $"{pair.Key}={pair.Value}"))
            + $"; __Host-decisya-session={bobSessionCookie}";
        callbackRequest.Headers.TryAddWithoutValidation("Cookie", plantedCookieHeader);

        using var callbackResponse = await aliceBffClient.SendAsync(callbackRequest, cancellationToken);
        callbackResponse.StatusCode.Should().Be(HttpStatusCode.Found, "dev-alice's sign-in should still complete");

        aliceJar.Cookies.TryGetValue("__Host-decisya-session", out var aliceSessionCookie).Should().BeTrue(
            "the callback should have issued dev-alice her own session cookie");
        var aliceSessionKey = SessionKeyExtractor.Extract(factory.Services, aliceSessionCookie!);
        aliceSessionKey.Should().NotBeNull();

        // 1) The new session key differs from the planted one.
        aliceSessionKey.Should().NotBe(bobSessionKey);

        // 2) The old (planted) cookie no longer authenticates as either user. A fresh,
        // jar-less client is used deliberately: aliceBffClient's own OriginCookieHandler
        // would otherwise also attach alice's own (now valid) session cookie alongside the
        // one set here, and ASP.NET Core would merge both "Cookie" header values.
        using var rawClient = LoginFlowHarness.CreateRawBffClient(factory);
        using var oldCookieRequest = new HttpRequestMessage(HttpMethod.Get, "/bff/me");
        oldCookieRequest.Headers.TryAddWithoutValidation("Cookie", $"__Host-decisya-session={bobSessionCookie}");
        using var oldCookieResponse = await rawClient.SendAsync(oldCookieRequest, cancellationToken);
        using var oldCookieDocument = JsonDocument.Parse(await oldCookieResponse.Content.ReadAsStringAsync(cancellationToken));
        oldCookieDocument.RootElement.GetProperty("isAuthenticated").GetBoolean().Should().BeFalse();

        // 3) dev-bob's old ticket is gone from Redis.
        (await ticketStore.RetrieveAsync(bobSessionKey!)).Should().BeNull();
    }
}
