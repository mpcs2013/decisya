using System.Net;

namespace Decisya.Bff.Tests;

/// <summary>
/// #341 G1 Story 1 "A request without antiforgery is refused" (AC-02-1), proven where the check lives: the Api accepts
/// bearer tokens only and the BFF's generic check covers every non-safe verb on <c>/api/*</c> (G2 "Antiforgery").
/// One ledger POST and the two ledger PUTs: without the header the BFF answers 403 and the Api double receives
/// nothing (so nothing is stored); with a valid pair the request is forwarded with the bearer token attached
/// server-side, no cookie and no antiforgery header.
/// </summary>
[Trait("Category", "Integration")]
public class LedgerApiAntiforgeryTests
{
    private const string AccountId = "0194a1c2-7d3e-7b4f-8a5c-1d2e3f4a5b6c";

    private readonly KeycloakBffFixture _keycloakFixture;
    private readonly RedisFixture _redisFixture;

    public LedgerApiAntiforgeryTests(KeycloakBffFixture keycloakFixture, RedisFixture redisFixture)
    {
        _keycloakFixture = keycloakFixture;
        _redisFixture = redisFixture;
    }

    public static TheoryData<string, string, string> LedgerWrites => new()
    {
        { "POST", "/api/ledger/accounts", "{\"name\":\"Everyday\",\"currency\":\"EUR\"}" },
        { "PUT", $"/api/ledger/accounts/{AccountId}/name", "{\"name\":\"Household\"}" },
        { "PUT", $"/api/ledger/accounts/{AccountId}/currency", "{\"currency\":\"JPY\",\"openingBalance\":null,\"openingDate\":null}" },
    };

    private static HttpRequestMessage Write(string method, string path, string body) =>
        new(new HttpMethod(method), path)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

    [Theory]
    [MemberData(nameof(LedgerWrites))]
    public async Task A_ledger_write_without_the_antiforgery_header_is_refused_and_the_api_receives_nothing(
        string method, string path, string body)
    {
        var ct = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(ct);
        await _redisFixture.EnsureStartedAsync(ct);
        await using var apiDouble = await ApiDouble.StartAsync(ct);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, apiAddress: apiDouble.Address);
        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", ct);

        using var request = Write(method, path, body);
        using var response = await result.BffClient.SendAsync(request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        apiDouble.Requests.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(LedgerWrites))]
    public async Task A_ledger_write_with_a_valid_antiforgery_pair_is_forwarded_with_the_bearer_token_only(
        string method, string path, string body)
    {
        var ct = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(ct);
        await _redisFixture.EnsureStartedAsync(ct);
        await using var apiDouble = await ApiDouble.StartAsync(ct);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, apiAddress: apiDouble.Address);
        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", ct);
        using (var me = await result.BffClient.GetAsync("/bff/me", ct))
        {
            me.EnsureSuccessStatusCode();
        }

        result.BffJar.Cookies.TryGetValue("__Host-decisya-xsrf", out var xsrf).Should().BeTrue();
        using var request = Write(method, path, body);
        request.Headers.Add("X-XSRF-TOKEN", xsrf);
        using var response = await result.BffClient.SendAsync(request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the API double answers 200 to everything");
        var forwarded = apiDouble.Requests.Should().ContainSingle().Which;
        forwarded.Method.Should().Be(method);
        forwarded.Path.Should().Be(path);
        forwarded.Headers["Authorization"].Single().Should().StartWith("Bearer ");
        forwarded.Headers.Should().NotContainKey("Cookie");
        forwarded.Headers.Should().NotContainKey("X-XSRF-TOKEN");
    }
}
