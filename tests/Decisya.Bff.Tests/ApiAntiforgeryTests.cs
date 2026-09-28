using System.Net;

namespace Decisya.Bff.Tests;

/// <summary>
/// #19 G4-19-03 (T-06, T-07; B-3, Stories 6 and 7): state-changing <c>/api</c> calls need the
/// same antiforgery pair as <c>/bff</c>, and an anonymous <c>/api</c> call gets 401, never a
/// redirect.
/// </summary>
[Trait("Category", "Integration")]
public class ApiAntiforgeryTests
{
    private readonly KeycloakBffFixture _keycloakFixture;
    private readonly RedisFixture _redisFixture;

    public ApiAntiforgeryTests(KeycloakBffFixture keycloakFixture, RedisFixture redisFixture)
    {
        _keycloakFixture = keycloakFixture;
        _redisFixture = redisFixture;
    }

    [Fact]
    public async Task A_state_changing_api_request_without_an_antiforgery_header_is_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);

        await using var apiDouble = await ApiDouble.StartAsync(cancellationToken);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, apiAddress: apiDouble.Address);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);

        using var response = await result.BffClient.PostAsync("/api/x", content: null, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        apiDouble.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_state_changing_api_request_with_a_mismatched_antiforgery_header_is_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);

        await using var apiDouble = await ApiDouble.StartAsync(cancellationToken);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, apiAddress: apiDouble.Address);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);
        // Ensures the antiforgery cookie pair has actually been issued before this attempt.
        using (var meResponse = await result.BffClient.GetAsync("/bff/me", cancellationToken))
        {
            meResponse.EnsureSuccessStatusCode();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/x");
        request.Headers.Add("X-XSRF-TOKEN", Canaries.Unique("mismatched-xsrf-token"));
        using var response = await result.BffClient.SendAsync(request, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        apiDouble.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_state_changing_api_request_with_a_valid_antiforgery_pair_is_forwarded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);

        await using var apiDouble = await ApiDouble.StartAsync(cancellationToken);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, apiAddress: apiDouble.Address);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);
        using (var meResponse = await result.BffClient.GetAsync("/bff/me", cancellationToken))
        {
            meResponse.EnsureSuccessStatusCode();
        }

        result.BffJar.Cookies.TryGetValue("__Host-decisya-xsrf", out var xsrfToken).Should().BeTrue();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/x");
        request.Headers.Add("X-XSRF-TOKEN", xsrfToken);
        using var response = await result.BffClient.SendAsync(request, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        apiDouble.Requests.Should().HaveCount(1);
        apiDouble.Requests.Single().Method.Should().Be("POST");
    }

    [Fact]
    public async Task An_anonymous_GET_to_api_gets_401_never_a_redirect()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);

        await using var apiDouble = await ApiDouble.StartAsync(cancellationToken);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, apiAddress: apiDouble.Address);
        using var client = LoginFlowHarness.CreateRawBffClient(factory);

        using var response = await client.GetAsync("/api/x", cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        ((int)response.StatusCode).Should().NotBe(302);
        apiDouble.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_anonymous_state_changing_api_request_gets_401_not_403()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);

        await using var apiDouble = await ApiDouble.StartAsync(cancellationToken);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, apiAddress: apiDouble.Address);
        using var client = LoginFlowHarness.CreateRawBffClient(factory);

        using var response = await client.PostAsync("/api/x", content: null, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        apiDouble.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_verb_outside_the_routes_allow_list_is_not_forwarded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);

        await using var apiDouble = await ApiDouble.StartAsync(cancellationToken);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, apiAddress: apiDouble.Address);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);

        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/api/x");
        using var response = await result.BffClient.SendAsync(request, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed, "the route only matches GET/HEAD/POST/PUT/PATCH/DELETE");
        apiDouble.Requests.Should().BeEmpty();
    }
}
