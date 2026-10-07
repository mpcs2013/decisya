using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Decisya.ServiceDefaults.Tests.Production;

/// <summary>
/// Issue #120, T120-02, D6: outside Development health answers on the management port only,
/// and the test is the local port, never the <c>Host</c> header.
/// </summary>
[Collection(RealAspNetCoreHostCollectionDefinition.Name)]
public class ManagementHealthTests
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task Health_is_404_on_the_application_port_outside_Development(string path)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = await ProductionTestHost.StartAsync(Environments.Production, cancellationToken: cancellationToken);

        using var response = await ProductionTestHost.GetAsync(
            host.AppUri, path, ProductionTestHost.AllowedHost, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task Health_is_200_on_the_management_port_outside_Development(string path)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = await ProductionTestHost.StartAsync(Environments.Production, cancellationToken: cancellationToken);

        using var response = await ProductionTestHost.GetAsync(host.ManagementUri, path, host: null, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellationToken)).Should().Be("Healthy");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task The_management_port_ignores_AllowedHosts_and_serves_nothing_else()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = await ProductionTestHost.StartAsync(Environments.Production, cancellationToken: cancellationToken);

        using var ping = await ProductionTestHost.GetAsync(host.ManagementUri, "/ping", host: null, cancellationToken);
        using var post = new HttpRequestMessage(HttpMethod.Post, new Uri(host.ManagementUri, "/health"));
        using var client = ProductionTestHost.NewClient();
        using var postResponse = await client.SendAsync(post, cancellationToken);

        ping.StatusCode.Should().Be(HttpStatusCode.NotFound);
        postResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // The red test for T120-02. The first version of the design matched the Host header
    // (RequireHost("*:8081")); a LAN client could then send "Host: app:8081" through Caddy
    // and reach the health checks. Both a host that AllowedHosts admits and one it refuses
    // must get no health response on the application port.
    [Theory]
    [InlineData("x:8081")]
    [InlineData("app.test:8081")]
    [InlineData("localhost:8081")]
    public async Task A_Host_header_naming_the_management_port_gets_no_health_response_on_the_application_port(string hostHeader)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        // The management port is a free port the test picked (8081 may be busy on a dev machine);
        // the header names 8081 either way, which is exactly what a header-based check would key on.
        await using var host = await ProductionTestHost.StartAsync(Environments.Production, cancellationToken: cancellationToken);

        using var response = await ProductionTestHost.GetAsync(host.AppUri, "/health", hostHeader, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
        body.Should().NotContain("Healthy");
    }

    [Fact]
    public async Task An_unhealthy_check_gives_503_with_the_status_word_only()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var detail = Canaries.Unique("check-detail");
        await using var host = await ProductionTestHost.StartAsync(
            Environments.Production,
            configureBuilder: builder => builder.Services.AddHealthChecks()
                .AddCheck("secret-check-name", () => HealthCheckResult.Unhealthy(detail, new InvalidOperationException(detail))),
            cancellationToken: cancellationToken);

        using var health = await ProductionTestHost.GetAsync(host.ManagementUri, "/health", host: null, cancellationToken);
        using var alive = await ProductionTestHost.GetAsync(host.ManagementUri, "/alive", host: null, cancellationToken);
        var body = await health.Content.ReadAsStringAsync(cancellationToken);

        health.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.Should().Be("Unhealthy");
        body.Should().NotContain(detail).And.NotContain("secret-check-name");
        alive.StatusCode.Should().Be(HttpStatusCode.OK, "/alive runs the live-tagged checks only");
    }

    [Fact]
    public async Task In_Development_health_is_mapped_on_the_application_port()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = await ProductionTestHost.StartAsync(Environments.Development, cancellationToken: cancellationToken);

        using var response = await ProductionTestHost.GetAsync(
            host.AppUri, "/health", ProductionTestHost.AllowedHost, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
