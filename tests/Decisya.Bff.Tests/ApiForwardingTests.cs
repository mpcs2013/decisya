using System.Net;
using Decisya.Bff.Session;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Decisya.Bff.Tests;

/// <summary>
/// #19 G4-19-01 (T-01, T-02, T-03; Story 1): what the BFF attaches to a forwarded request, and
/// what it strips.
/// </summary>
[Trait("Category", "Integration")]
public class ApiForwardingTests
{
    private readonly KeycloakBffFixture _keycloakFixture;
    private readonly RedisFixture _redisFixture;

    public ApiForwardingTests(KeycloakBffFixture keycloakFixture, RedisFixture redisFixture)
    {
        _keycloakFixture = keycloakFixture;
        _redisFixture = redisFixture;
    }

    [Fact]
    public async Task Forwarded_request_carries_only_the_bffs_bearer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);

        await using var apiDouble = await ApiDouble.StartAsync(cancellationToken);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, apiAddress: apiDouble.Address);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);
        result.BffJar.Cookies.TryGetValue("__Host-decisya-session", out var sessionCookie).Should().BeTrue();
        var sessionKey = SessionKeyExtractor.Extract(factory.Services, sessionCookie!)!;
        var ticketStore = factory.Services.GetRequiredService<RedisTicketStore>();
        var accessToken = (await ticketStore.RetrieveAsync(sessionKey))!.Properties.GetTokenValue("access_token");

        using (var meResponse = await result.BffClient.GetAsync("/bff/me", cancellationToken))
        {
            meResponse.EnsureSuccessStatusCode();
        }

        result.BffJar.Cookies.TryGetValue("__Host-decisya-xsrf", out var xsrfToken).Should().BeTrue();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/x");
        request.Headers.Add("X-XSRF-TOKEN", xsrfToken);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer attacker-supplied-value");
        request.Headers.TryAddWithoutValidation("Forwarded", "for=6.6.6.6");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "6.6.6.6");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "evil.test");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "http");

        using var response = await result.BffClient.SendAsync(request, cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        apiDouble.Requests.Should().HaveCount(1);
        var forwarded = apiDouble.Requests.Single();

        forwarded.Headers.Should().ContainKey("Authorization");
        forwarded.Headers["Authorization"].Should().ContainSingle().Which.Should().Be($"Bearer {accessToken}");

        forwarded.Headers.Should().NotContainKey("Cookie");
        forwarded.Headers.Should().NotContainKey("X-XSRF-TOKEN");
        forwarded.Headers.Should().NotContainKey("Forwarded");

        // "Set", not "Append": whatever YARP computes for the immediate connection replaces
        // the client-supplied value outright — it is never present in a header that still
        // carries the spoofed value (the header may be entirely absent under the in-process
        // test harness, which has no real socket to derive a remote IP from).
        if (forwarded.Headers.TryGetValue("X-Forwarded-For", out var xForwardedForValues))
        {
            xForwardedForValues.Should().NotContain(value => value.Contains("6.6.6.6", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_GET_request_needs_no_antiforgery_header_and_is_forwarded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);

        await using var apiDouble = await ApiDouble.StartAsync(cancellationToken);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, apiAddress: apiDouble.Address);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);

        using var response = await result.BffClient.GetAsync("/api/x", cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        apiDouble.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task The_forwarded_path_always_starts_with_api_and_the_client_supplied_Host_is_not_used_to_steer_the_destination()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);

        await using var apiDouble = await ApiDouble.StartAsync(cancellationToken);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, apiAddress: apiDouble.Address);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/widgets/42");
        request.Headers.Host = "evil.test";

        using var response = await result.BffClient.SendAsync(request, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the double is the only possible destination regardless of the Host header");
        apiDouble.Requests.Should().ContainSingle(recorded => recorded.Path == "/api/widgets/42");
    }
}
