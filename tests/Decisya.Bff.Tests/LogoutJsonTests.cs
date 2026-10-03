using System.Net;
using System.Text.Json;
using Decisya.Bff.Session;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Decisya.Bff.Tests;

/// <summary>
/// #26 G2 D4 and Story 3: the JSON variant of <c>POST /bff/logout</c> against the real handler, a
/// real Keycloak and Redis. Only an <c>Accept</c> that names exactly <c>application/json</c> with a
/// non-zero quality gets the 200 body; every other caller keeps the #18 302. The antiforgery check,
/// the B-2 end-session call and the ticket deletion are the same in both variants.
/// </summary>
[Trait("Category", "Integration")]
public class LogoutJsonTests
{
    private static readonly string[] TokenNames = ["access_token", "id_token", "refresh_token"];

    private readonly KeycloakBffFixture _keycloakFixture;
    private readonly RedisFixture _redisFixture;

    public LogoutJsonTests(KeycloakBffFixture keycloakFixture, RedisFixture redisFixture)
    {
        _keycloakFixture = keycloakFixture;
        _redisFixture = redisFixture;
    }

    [Fact]
    public async Task Accept_application_json_gets_200_with_only_the_end_session_URL_and_the_session_is_gone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);
        var countingHandler = new CountingBackchannelHandler(new HttpClientHandler());
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, backchannelHttpHandler: countingHandler);
        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/", cancellationToken);
        result.BffJar.Cookies.TryGetValue("__Host-decisya-session", out var sessionCookie).Should().BeTrue();
        var sessionKey = SessionKeyExtractor.Extract(factory.Services, sessionCookie!)!;
        var ticketStore = factory.Services.GetRequiredService<RedisTicketStore>();
        var ticket = await ticketStore.RetrieveAsync(sessionKey);
        ticket.Should().NotBeNull();
        var tokens = TokenNames
            .Select(name => ticket!.Properties.GetTokenValue(name))
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(value => value!)
            .ToList();
        tokens.Should().NotBeEmpty();
        var xsrf = await GetXsrfAsync(result, cancellationToken);

        var exchange = await LogoutAsync(result, xsrf, "application/json", cancellationToken);

        exchange.StatusCode.Should().Be(HttpStatusCode.OK);
        exchange.Headers.Should().NotContainKey("Location");
        exchange.Headers["Content-Type"].Single().Should().Be("application/json; charset=utf-8");
        exchange.Headers["Vary"].Single().Should().Contain("Accept");
        exchange.Headers["Cache-Control"].Single().Should().Contain("no-store");

        using var document = JsonDocument.Parse(exchange.Body);
        document.RootElement.EnumerateObject().Select(property => property.Name).Should().Equal("redirectUri");
        var redirectUri = document.RootElement.GetProperty("redirectUri").GetString()!;
        var uri = new Uri(redirectUri, UriKind.Absolute);
        uri.GetLeftPart(UriPartial.Authority).Should().Be(new Uri(_keycloakFixture.Authority).GetLeftPart(UriPartial.Authority));
        var query = QueryHelpers.ParseQuery(uri.Query);
        query["client_id"].ToString().Should().Be("decisya-bff");
        query["post_logout_redirect_uri"].ToString().Should().EndWith("/signout-callback-oidc");
        query["state"].ToString().Should().NotBeNullOrEmpty();
        query.ContainsKey("id_token_hint").Should().BeFalse();
        foreach (var token in tokens)
        {
            exchange.Body.Should().NotContain(token);
            redirectUri.Should().NotContain(token);
        }

        var cleared = SetCookieParser.ParseAll(exchange.Headers).SingleOrDefault(cookie => cookie.Name == "__Host-decisya-session");
        cleared.Should().NotBeNull("the session cookie is cleared in the same response");
        cleared!.HasExpiry.Should().BeTrue();
        (await ticketStore.RetrieveAsync(sessionKey)).Should().BeNull("the ticket is deleted in the same request");
        countingHandler.EndSessionCallCount.Should().Be(1, "exactly one Keycloak end-session call, as for the 302 variant");

        // The signed-out callback lands the browser on "/" (RedirectUri), without a second hop.
        using var rawClient = LoginFlowHarness.CreateRawBffClient(factory);
        using var callback = await rawClient.GetAsync(
            "/signout-callback-oidc?state=" + Uri.EscapeDataString(query["state"].ToString()), cancellationToken);
        callback.StatusCode.Should().Be(HttpStatusCode.Found);
        callback.Headers.Location!.OriginalString.Should().Be("/");
    }

    [Theory]
    [InlineData("*/*")]
    [InlineData("text/html")]
    [InlineData("application/json;q=0")]
    [InlineData("application/*")]
    [InlineData("application/problem+json")]
    [InlineData(null)]
    public async Task Any_other_Accept_keeps_the_302_to_the_end_session_endpoint(string? accept)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);
        var countingHandler = new CountingBackchannelHandler(new HttpClientHandler());
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, backchannelHttpHandler: countingHandler);
        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/", cancellationToken);
        var xsrf = await GetXsrfAsync(result, cancellationToken);

        var exchange = await LogoutAsync(result, xsrf, accept, cancellationToken);

        exchange.StatusCode.Should().Be(HttpStatusCode.Found);
        var location = exchange.Headers["Location"].Single();
        location.Should().Contain("client_id=decisya-bff").And.NotContain("id_token_hint");
        new Uri(location, UriKind.Absolute).GetLeftPart(UriPartial.Authority)
            .Should().Be(new Uri(_keycloakFixture.Authority).GetLeftPart(UriPartial.Authority));
        exchange.Body.Should().BeEmpty();
        countingHandler.EndSessionCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Accept_json_without_the_antiforgery_header_is_403_and_the_session_stays_valid()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture);
        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/", cancellationToken);
        _ = await GetXsrfAsync(result, cancellationToken);

        var exchange = await LogoutAsync(result, null, "application/json", cancellationToken);

        exchange.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        exchange.Headers.Should().NotContainKey("Location");
        using var me = await result.BffClient.GetAsync("/bff/me", cancellationToken);
        using var document = JsonDocument.Parse(await me.Content.ReadAsStringAsync(cancellationToken));
        document.RootElement.GetProperty("isAuthenticated").GetBoolean().Should().BeTrue("a refused logout never ends the session");
    }

    [Fact]
    public async Task Accept_json_without_a_session_is_refused_and_never_redirects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture);
        using var client = LoginFlowHarness.CreateRawBffClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/logout");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await client.SendAsync(request, cancellationToken);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        response.Headers.Contains("Location").Should().BeFalse();
    }

    private static async Task<string> GetXsrfAsync(LoginFlowResult result, CancellationToken cancellationToken)
    {
        using var me = await result.BffClient.GetAsync("/bff/me", cancellationToken);
        me.EnsureSuccessStatusCode();
        result.BffJar.Cookies.TryGetValue("__Host-decisya-xsrf", out var xsrf).Should().BeTrue();
        return xsrf!;
    }

    private static async Task<CapturedExchange> LogoutAsync(
        LoginFlowResult result, string? xsrf, string? accept, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/logout");
        if (xsrf is not null)
        {
            request.Headers.Add("X-XSRF-TOKEN", xsrf);
        }

        if (accept is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept", accept);
        }

        using var response = await result.BffClient.SendAsync(request, cancellationToken);
        return await LoginFlowHarness.CaptureAsync("bff-logout", response, cancellationToken);
    }
}
