using Decisya.Bff.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Decisya.Bff.Tests;

/// <summary>
/// G4-18-02's endpoint-metadata check: every non-GET <c>/bff</c> endpoint either carries
/// <see cref="AntiforgeryRequiredMetadata"/> or is <c>/bff/backchannel-logout</c> (which
/// carries the opt-out instead); the opt-out appears on exactly one endpoint. Pure reflection
/// over the built <c>EndpointDataSource</c>: no Docker.
/// </summary>
public class AntiforgeryFilterMetadataTests
{
    [Fact]
    public void Every_non_GET_bff_endpoint_requires_antiforgery_except_the_one_opted_out_backchannel_logout_endpoint()
    {
        using var factory = new DevelopmentFactory();
        var endpointDataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        var bffEndpoints = endpointDataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/bff", StringComparison.Ordinal) == true)
            .ToList();

        bffEndpoints.Should().NotBeEmpty();

        var nonGetEndpoints = bffEndpoints
            .Where(endpoint => !EndpointAllowsOnlyGet(endpoint))
            .ToList();
        nonGetEndpoints.Should().NotBeEmpty();

        foreach (var endpoint in nonGetEndpoints)
        {
            var hasRequiredMetadata = endpoint.Metadata.GetMetadata<AntiforgeryRequiredMetadata>() is not null;
            var hasOptOut = endpoint.Metadata.GetMetadata<SkipAntiforgeryMetadata>() is not null;
            var isBackchannelLogout = string.Equals(endpoint.RoutePattern.RawText, "/bff/backchannel-logout", StringComparison.Ordinal);

            (hasRequiredMetadata || isBackchannelLogout).Should().BeTrue(
                $"{endpoint.RoutePattern.RawText} is non-GET and must carry the antiforgery filter metadata, " +
                "unless it is /bff/backchannel-logout");

            if (isBackchannelLogout)
            {
                hasOptOut.Should().BeTrue("/bff/backchannel-logout must carry the explicit opt-out metadata");
            }
        }

        var endpointsWithOptOut = bffEndpoints.Count(endpoint => endpoint.Metadata.GetMetadata<SkipAntiforgeryMetadata>() is not null);
        endpointsWithOptOut.Should().Be(1, "the antiforgery opt-out must appear on exactly one endpoint");
    }

    private static bool EndpointAllowsOnlyGet(RouteEndpoint endpoint)
    {
        var methodsMetadata = endpoint.Metadata.GetMetadata<HttpMethodMetadata>();
        if (methodsMetadata is null)
        {
            // No HttpMethodMetadata means every method is accepted (e.g. a route with no
            // MapGet/MapPost restriction); treat conservatively as "not GET-only".
            return false;
        }

        return methodsMetadata.HttpMethods.All(method => string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class DevelopmentFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
                configurationBuilder.AddInMemoryCollection(TestConfiguration.GoodOverrides()));
        }
    }
}
