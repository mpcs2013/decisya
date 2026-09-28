using System.Net;

namespace Decisya.Bff.Tests;

/// <summary>
/// #19 G4-19-02 (T-04, T-05; NFR-21): nothing from the upstream response can set state or
/// carry a token back to the browser.
/// </summary>
[Trait("Category", "Integration")]
public class ApiResponseTests
{
    private readonly KeycloakBffFixture _keycloakFixture;
    private readonly RedisFixture _redisFixture;

    public ApiResponseTests(KeycloakBffFixture keycloakFixture, RedisFixture redisFixture)
    {
        _keycloakFixture = keycloakFixture;
        _redisFixture = redisFixture;
    }

    [Fact]
    public async Task Upstream_set_cookie_is_dropped_and_the_session_still_authenticates_afterwards()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);

        await using var apiDouble = await ApiDouble.StartAsync(cancellationToken);
        apiDouble.RespondWithSetCookie = true;
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, apiAddress: apiDouble.Address);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);

        using var response = await result.BffClient.GetAsync("/api/x", cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("Set-Cookie", out _).Should().BeFalse("the API must never set a cookie on the BFF's own origin");

        using var meResponse = await result.BffClient.GetAsync("/bff/me", cancellationToken);
        meResponse.StatusCode.Should().Be(HttpStatusCode.OK, "the session must still authenticate afterwards");
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("text/html")]
    [InlineData("application/problem+json")]
    public async Task Upstream_5xx_body_is_replaced_with_the_generic_ProblemDetails(string upstreamContentType)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);

        await using var apiDouble = await ApiDouble.StartAsync(cancellationToken);
        apiDouble.RespondWith5xxLeakingAuthorization = true;
        apiDouble.LeakingBodyContentType = upstreamContentType;
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, apiAddress: apiDouble.Address);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);

        using var response = await result.BffClient.GetAsync("/api/x", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "the status code is kept");
        body.Should().NotContain("Bearer", "the body must not echo the forwarded Authorization value");
        body.Should().NotContain("SECRETTOKEN");
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }
}
