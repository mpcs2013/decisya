using System.Net;
using Decisya.ServiceDefaults.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Decisya.Api.Tests;

/// <summary>
/// Story 2 (health/liveness) and Story 5 scenario 4 (the skeleton exposes only the two
/// health endpoints, anonymous). G4-15-01, G4-15-02. #20 (G2 "Existing tests to update"):
/// outside Development, /alive and /health now answer 401 (the fallback policy, since no
/// endpoint exists there any more); the Production endpoint set is exactly /api/whoami; the
/// Development set is /health, /alive and /api/whoami; only the health pair carries
/// IAllowAnonymous.
/// </summary>
public class HealthEndpointTests
{
    [Fact]
    public async Task Alive_and_health_return_200_in_Development_when_every_check_is_healthy()
    {
        await using var factory = CreateFactory("Development");
        using var client = factory.CreateClient();

        (await client.GetAsync("/alive", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/health", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_returns_503_with_the_default_body_and_alive_still_returns_200()
    {
        var canary = Canaries.Unique("health-check-detail");
        await using var factory = CreateFactory("Development", services =>
        {
            services.AddHealthChecks().AddCheck(
                "failing",
                () => HealthCheckResult.Unhealthy(canary, new InvalidOperationException(canary)));
        });
        using var client = factory.CreateClient();

        var healthResponse = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        healthResponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await healthResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Be("Unhealthy");
        body.Should().NotContain(canary);

        (await client.GetAsync("/alive", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Test")]
    public async Task Health_endpoints_return_401_outside_Development(string environmentName)
    {
        await using var factory = CreateFactory(environmentName);
        using var client = factory.CreateClient();

        (await client.GetAsync("/alive", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/health", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_Production_endpoint_set_is_exactly_api_whoami_and_the_tenancy_endpoints()
    {
        await using var factory = CreateFactory("Production");
        using var scope = factory.Services.CreateScope();

        var dataSource = scope.ServiceProvider.GetRequiredService<EndpointDataSource>();

        // #21: the Tenancy module's two endpoints join /api/whoami; they carry no
        // AllowAnonymous metadata of their own, so they still require the fallback policy.
        dataSource.Endpoints.OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText)
            .Should().BeEquivalentTo(["/api/whoami", "/api/tenancy/me", "/api/tenancy/members"]);
    }

    [Fact]
    public async Task The_Development_endpoint_set_is_health_alive_api_whoami_and_the_tenancy_endpoints_and_only_the_health_pair_is_anonymous()
    {
        await using var factory = CreateFactory("Development");
        using var scope = factory.Services.CreateScope();

        var dataSource = scope.ServiceProvider.GetRequiredService<EndpointDataSource>();
        var routeEndpoints = dataSource.Endpoints.OfType<RouteEndpoint>().ToArray();

        routeEndpoints.Select(e => e.RoutePattern.RawText).Should().BeEquivalentTo(
            ["/health", "/alive", "/api/whoami", "/api/tenancy/me", "/api/tenancy/members"]);

        foreach (var endpoint in routeEndpoints)
        {
            var isHealthPair = endpoint.RoutePattern.RawText is "/health" or "/alive";
            var isAnonymous = endpoint.Metadata.OfType<IAllowAnonymous>().Any();
            isAnonymous.Should().Be(isHealthPair, endpoint.RoutePattern.RawText);
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string environmentName, Action<IServiceCollection>? configureServices = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environmentName);
            // #21, G2: Program.cs reads ConnectionStrings:tenancy eagerly, before Build() — see
            // Decisya.Api.Tests.Authentication.ApiTestFactory's remarks for why that requires
            // UseSetting, not ConfigureAppConfiguration.
            builder.UseSetting(
                Decisya.Api.Tests.Authentication.ApiTestFactory.PlaceholderTenancyConnectionStringKey,
                Decisya.Api.Tests.Authentication.ApiTestFactory.PlaceholderTenancyConnectionString);
            builder.UseSetting(
                Decisya.Api.Tests.Authentication.ApiTestFactory.PlaceholderEntitlementsConnectionStringKey,
                Decisya.Api.Tests.Authentication.ApiTestFactory.PlaceholderEntitlementsConnectionString);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            [
                new(DecisyaObservabilityOptions.UserIdHashKeyPath, Canaries.HashKey()),
                new("Api:Jwt:Authority", "https://issuer.test/realms/decisya"),
            ]));

            if (configureServices is not null)
            {
                builder.ConfigureServices(configureServices);
            }
        });
}
