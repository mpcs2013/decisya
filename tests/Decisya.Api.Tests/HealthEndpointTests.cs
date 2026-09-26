using System.Net;
using Decisya.ServiceDefaults.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;

namespace Decisya.Api.Tests;

/// <summary>
/// Story 2 (health/liveness) and Story 5 scenario 4 (the skeleton exposes only the two
/// health endpoints, anonymous). G4-15-01, G4-15-02.
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
    public async Task Health_endpoints_return_404_outside_Development(string environmentName)
    {
        await using var factory = CreateFactory(environmentName);
        using var client = factory.CreateClient();

        (await client.GetAsync("/alive", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/health", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task No_endpoint_is_mapped_outside_Development()
    {
        await using var factory = CreateFactory("Production");
        using var scope = factory.Services.CreateScope();

        var dataSource = scope.ServiceProvider.GetRequiredService<EndpointDataSource>();

        dataSource.Endpoints.Should().BeEmpty();
    }

    [Fact]
    public async Task Only_health_and_alive_are_exposed_and_neither_requires_authentication()
    {
        await using var factory = CreateFactory("Development");
        using var scope = factory.Services.CreateScope();

        var dataSource = scope.ServiceProvider.GetRequiredService<EndpointDataSource>();
        var routeEndpoints = dataSource.Endpoints.OfType<RouteEndpoint>().ToArray();

        routeEndpoints.Select(e => e.RoutePattern.RawText).Should().BeEquivalentTo(["/health", "/alive"]);
        foreach (var endpoint in routeEndpoints)
        {
            endpoint.Metadata.OfType<IAuthorizeData>().Should().BeEmpty();
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string environmentName, Action<IServiceCollection>? configureServices = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environmentName);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            [
                new(DecisyaObservabilityOptions.UserIdHashKeyPath, Canaries.HashKey()),
            ]));

            if (configureServices is not null)
            {
                builder.ConfigureServices(configureServices);
            }
        });
}
