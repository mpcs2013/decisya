using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Decisya.ServiceDefaults.Tests.Production;

/// <summary>Issue #120, D6: the <c>--health-probe</c> mode and its exit codes.</summary>
[Collection(RealAspNetCoreHostCollectionDefinition.Name)]
public class HealthProbeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    [Theory]
    [InlineData(new[] { "--health-probe" }, true)]
    [InlineData(new[] { "--urls", "http://x", "--health-probe" }, true)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--health-probe=1" }, false)]
    [InlineData(new[] { "--HEALTH-PROBE" }, false)]
    public void IsRequested_matches_the_exact_argument_only(string[] args, bool expected)
    {
        HealthProbe.IsRequested(args).Should().Be(expected);
    }

    [Fact]
    public void The_exit_codes_are_0_and_1()
    {
        HealthProbe.ExitHealthy.Should().Be(0);
        HealthProbe.ExitUnhealthy.Should().Be(1);
    }

    [Fact]
    public async Task A_healthy_service_exits_0()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = await ProductionTestHost.StartAsync(Environments.Production, cancellationToken: cancellationToken);

        var exit = await HealthProbe.RunAsync(host.ManagementUri.Port, Timeout, cancellationToken);

        exit.Should().Be(HealthProbe.ExitHealthy);
    }

    [Fact]
    public async Task An_unhealthy_service_exits_1()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = await ProductionTestHost.StartAsync(
            Environments.Production,
            configureBuilder: builder => builder.Services.AddHealthChecks()
                .AddCheck("down", () => HealthCheckResult.Unhealthy("down")),
            cancellationToken: cancellationToken);

        var exit = await HealthProbe.RunAsync(host.ManagementUri.Port, Timeout, cancellationToken);

        exit.Should().Be(HealthProbe.ExitUnhealthy);
    }

    [Fact]
    public async Task Nothing_listening_exits_1()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var exit = await HealthProbe.RunAsync(ProductionTestHost.FreePort(), Timeout, cancellationToken);

        exit.Should().Be(HealthProbe.ExitUnhealthy);
    }

    [Theory]
    [InlineData(204)]
    [InlineData(302)]
    [InlineData(404)]
    public async Task Any_status_other_than_200_exits_1(int status)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var stub = await StartStubAsync(context =>
        {
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        }, cancellationToken);

        var exit = await HealthProbe.RunAsync(stub.Port, Timeout, cancellationToken);

        exit.Should().Be(HealthProbe.ExitUnhealthy);
    }

    [Fact]
    public async Task A_service_that_never_answers_times_out_and_exits_1()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var stub = await StartStubAsync(
            context => Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted), cancellationToken);

        var exit = await HealthProbe.RunAsync(stub.Port, TimeSpan.FromMilliseconds(300), cancellationToken);

        exit.Should().Be(HealthProbe.ExitUnhealthy);
    }

    [Fact]
    public async Task The_probe_asks_for_health_over_GET_and_nothing_else()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        string? seen = null;
        await using var stub = await StartStubAsync(context =>
        {
            seen = $"{context.Request.Method} {context.Request.Path}";
            return Task.CompletedTask;
        }, cancellationToken);

        await HealthProbe.RunAsync(stub.Port, Timeout, cancellationToken);

        seen.Should().Be("GET /health");
    }

    private static async Task<Stub> StartStubAsync(RequestDelegate handler, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(handler);
        await app.StartAsync(cancellationToken);
        return new Stub(app, new Uri(app.Urls.First()).Port);
    }

    private sealed class Stub(WebApplication app, int port) : IAsyncDisposable
    {
        public int Port { get; } = port;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
