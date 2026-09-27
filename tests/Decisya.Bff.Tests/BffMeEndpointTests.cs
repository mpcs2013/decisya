using System.Net;
using System.Text.Json;

namespace Decisya.Bff.Tests;

/// <summary>
/// G5 (issue #18 traceability): Story 5's own three scenarios — the shape of the <c>GET
/// /bff/me</c> response itself. Every other <c>Category=Integration</c> class only reads
/// <c>isAuthenticated</c> off this endpoint as a side effect; this class is the one that
/// asserts the identity claims (<see cref="MeResponse"/>) it carries, for a tenant user, an
/// anonymous caller, and a platform-admin.
/// </summary>
[Trait("Category", "Integration")]
public class BffMeEndpointTests
{
    private readonly KeycloakBffFixture _keycloakFixture;
    private readonly RedisFixture _redisFixture;

    public BffMeEndpointTests(KeycloakBffFixture keycloakFixture, RedisFixture redisFixture)
    {
        _keycloakFixture = keycloakFixture;
        _redisFixture = redisFixture;
    }

    [Fact]
    public async Task An_authenticated_user_gets_her_identity_claims()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-alice", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);

        using var meResponse = await result.BffClient.GetAsync("/bff/me", cancellationToken);

        meResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await meResponse.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;

        root.GetProperty("isAuthenticated").GetBoolean().Should().BeTrue();
        root.GetProperty("sub").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("email").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("tenantId").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("roles").EnumerateArray().Select(role => role.GetString()).Should().Contain("tenant-user");

        var body = await meResponse.Content.ReadAsStringAsync(cancellationToken);
        body.Should().NotContain("access_token").And.NotContain("id_token").And.NotContain("refresh_token");
    }

    [Fact]
    public async Task An_anonymous_caller_gets_a_non_error_not_signed_in_response()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture);
        using var client = LoginFlowHarness.CreateRawBffClient(factory);

        using var meResponse = await client.GetAsync("/bff/me", cancellationToken);

        meResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await meResponse.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;

        root.GetProperty("isAuthenticated").GetBoolean().Should().BeFalse();
        root.TryGetProperty("sub", out _).Should().BeFalse();
        root.TryGetProperty("email", out _).Should().BeFalse();
        root.TryGetProperty("tenantId", out _).Should().BeFalse();
        root.TryGetProperty("roles", out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_platform_admins_response_carries_no_tenant_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(cancellationToken);
        await _redisFixture.EnsureStartedAsync(cancellationToken);
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture);

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-admin", _keycloakFixture.DevUserPassword, "/dashboard", cancellationToken);

        using var meResponse = await result.BffClient.GetAsync("/bff/me", cancellationToken);

        meResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await meResponse.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;

        root.GetProperty("isAuthenticated").GetBoolean().Should().BeTrue();
        root.GetProperty("roles").EnumerateArray().Select(role => role.GetString()).Should().Contain("platform-admin");
        root.TryGetProperty("tenantId", out _).Should().BeFalse(
            "a platform-admin carries no tenant_id claim; the field must be omitted, not fabricated or empty");
    }
}
