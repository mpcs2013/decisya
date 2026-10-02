using System.Net;

namespace Decisya.Bff.Tests;

/// <summary>
/// #25 G3 G4-25-05 (T-12) and G1 Story 2 last scenario: the three admin verbs (POST, PUT, DELETE on
/// <c>/api/admin/tenants/{id}/...</c>) are covered by the BFF's generic antiforgery check for every
/// non-safe verb. No token, another session's pair and a mismatched header are refused before anything is
/// forwarded; a valid pair is forwarded with the access token attached server-side, no cookie and no
/// antiforgery header. A 4xx body from the API passes through and a 5xx body is replaced (#19).
/// </summary>
[Trait("Category", "Integration")]
public class AdminApiAntiforgeryTests
{
    private const string Tenant = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    private readonly KeycloakBffFixture _keycloakFixture;
    private readonly RedisFixture _redisFixture;

    public AdminApiAntiforgeryTests(KeycloakBffFixture keycloakFixture, RedisFixture redisFixture)
    {
        _keycloakFixture = keycloakFixture;
        _redisFixture = redisFixture;
    }

    public static TheoryData<string, string> AdminVerbs => new()
    {
        { "POST", $"/api/admin/tenants/{Tenant}/trial" },
        { "PUT", $"/api/admin/tenants/{Tenant}/overrides/forecasting.scenarios" },
        { "DELETE", $"/api/admin/tenants/{Tenant}/overrides/forecasting.scenarios" },
    };

    private static HttpRequestMessage Admin(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "PUT")
        {
            request.Content = new StringContent("{\"reason\":\"pilot\"}", System.Text.Encoding.UTF8, "application/json");
        }

        return request;
    }

    [Theory]
    [MemberData(nameof(AdminVerbs))]
    public async Task An_admin_call_without_the_antiforgery_header_is_refused_and_the_api_receives_nothing(string method, string path)
    {
        var ct = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(ct);
        await _redisFixture.EnsureStartedAsync(ct);
        await using var apiDouble = await ApiDouble.StartAsync(ct);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, apiAddress: apiDouble.Address);
        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-admin", _keycloakFixture.DevUserPassword, "/dashboard", ct);

        using var request = Admin(method, path);
        using var response = await result.BffClient.SendAsync(request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        apiDouble.Requests.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(AdminVerbs))]
    public async Task An_admin_call_with_another_sessions_antiforgery_pair_is_refused_and_the_api_receives_nothing(string method, string path)
    {
        var ct = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(ct);
        await _redisFixture.EnsureStartedAsync(ct);
        await using var apiDouble = await ApiDouble.StartAsync(ct);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, apiAddress: apiDouble.Address);

        var adminResult = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-admin", _keycloakFixture.DevUserPassword, "/dashboard", ct);
        adminResult.BffJar.Cookies.TryGetValue("__Host-decisya-session", out var adminSession).Should().BeTrue();

        var aliceResult = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", ct);
        using (var me = await aliceResult.BffClient.GetAsync("/bff/me", ct))
        {
            me.EnsureSuccessStatusCode();
        }

        aliceResult.BffJar.Cookies.TryGetValue("__Host-decisya-af", out var aliceAf).Should().BeTrue();
        aliceResult.BffJar.Cookies.TryGetValue("__Host-decisya-xsrf", out var aliceXsrf).Should().BeTrue();

        using var rawClient = LoginFlowHarness.CreateRawBffClient(factory);
        using var request = Admin(method, path);
        request.Headers.TryAddWithoutValidation("Cookie", $"__Host-decisya-session={adminSession}; __Host-decisya-af={aliceAf}");
        request.Headers.Add("X-XSRF-TOKEN", aliceXsrf);
        using var response = await rawClient.SendAsync(request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        apiDouble.Requests.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(AdminVerbs))]
    public async Task An_admin_call_with_a_mismatched_antiforgery_header_is_refused(string method, string path)
    {
        var ct = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(ct);
        await _redisFixture.EnsureStartedAsync(ct);
        await using var apiDouble = await ApiDouble.StartAsync(ct);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, apiAddress: apiDouble.Address);
        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-admin", _keycloakFixture.DevUserPassword, "/dashboard", ct);
        using (var me = await result.BffClient.GetAsync("/bff/me", ct))
        {
            me.EnsureSuccessStatusCode();
        }

        using var request = Admin(method, path);
        request.Headers.Add("X-XSRF-TOKEN", Canaries.Unique("mismatched-xsrf-token"));
        using var response = await result.BffClient.SendAsync(request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        apiDouble.Requests.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(AdminVerbs))]
    public async Task An_admin_call_with_a_valid_pair_is_forwarded_with_the_bearer_token_and_no_cookie_and_no_antiforgery_header(string method, string path)
    {
        var ct = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(ct);
        await _redisFixture.EnsureStartedAsync(ct);
        await using var apiDouble = await ApiDouble.StartAsync(ct);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, apiAddress: apiDouble.Address);
        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-admin", _keycloakFixture.DevUserPassword, "/dashboard", ct);
        using (var me = await result.BffClient.GetAsync("/bff/me", ct))
        {
            me.EnsureSuccessStatusCode();
        }

        result.BffJar.Cookies.TryGetValue("__Host-decisya-xsrf", out var xsrf).Should().BeTrue();
        using var request = Admin(method, path);
        request.Headers.Add("X-XSRF-TOKEN", xsrf);
        using var response = await result.BffClient.SendAsync(request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the API double answers 200 to everything");
        var forwarded = apiDouble.Requests.Should().ContainSingle().Which;
        forwarded.Method.Should().Be(method);
        forwarded.Path.Should().Be(path);
        forwarded.Headers.Should().ContainKey("Authorization");
        forwarded.Headers["Authorization"].Single().Should().StartWith("Bearer ");
        forwarded.Headers.Should().NotContainKey("Cookie");
        forwarded.Headers.Should().NotContainKey("X-XSRF-TOKEN");
    }
}
