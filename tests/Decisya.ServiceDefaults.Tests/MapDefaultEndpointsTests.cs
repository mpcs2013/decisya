using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Decisya.ServiceDefaults.Tests;

/// <summary>
/// This class and <see cref="Telemetry.HttpClientPropagationTests"/> are the only two
/// classes in this assembly that stand up a real, socket-bound <c>WebApplication</c>/generic
/// host (Kestrel on <c>127.0.0.1:0</c>). xUnit runs test classes in different collections
/// concurrently by default; two such hosts starting at the same instant reportedly raced
/// once in a solution-wide run (many test-host processes competing for the machine's CPU
/// and ephemeral ports at once) and made <c>MapHealthChecks</c> observe a
/// not-yet-fully-built service provider. Neither class shares mutable state or an explicit
/// resource, so this collection buys determinism by serializing them rather than by
/// asserting a specific shared culprit.
/// </summary>
[Collection(RealAspNetCoreHostCollectionDefinition.Name)]
public class MapDefaultEndpointsTests
{
    [Fact]
    public async Task Health_and_alive_both_carry_AllowAnonymous_metadata_in_Development()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Services.AddHealthChecks();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        await using var app = builder.Build();
        app.MapDefaultEndpoints();
        await app.StartAsync(cancellationToken);

        var dataSource = app.Services.GetRequiredService<EndpointDataSource>();
        var routeEndpoints = dataSource.Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText is "/health" or "/alive")
            .ToArray();

        await app.StopAsync(cancellationToken);

        routeEndpoints.Select(endpoint => endpoint.RoutePattern.RawText).Should().BeEquivalentTo(["/health", "/alive"]);
        foreach (var endpoint in routeEndpoints)
        {
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().Should().NotBeNull(
                $"{endpoint.RoutePattern.RawText} must stay reachable without a token once a host's fallback " +
                "policy requires an authenticated user (#20)");
        }
    }

    [Fact]
    public async Task Health_and_alive_are_not_mapped_outside_Development()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        await using var app = builder.Build();
        app.MapDefaultEndpoints();
        await app.StartAsync(cancellationToken);

        var dataSource = app.Services.GetRequiredService<EndpointDataSource>();
        var endpoints = dataSource.Endpoints;

        await app.StopAsync(cancellationToken);

        endpoints.Should().BeEmpty();
    }
}
