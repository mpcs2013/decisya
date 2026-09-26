using System.Diagnostics;
using Decisya.ServiceDefaults.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Decisya.ServiceDefaults.Tests.Telemetry;

/// <summary>Story 1 scenario 4: an outgoing HttpClient call made while a request is being
/// traced carries a W3C <c>traceparent</c> header.</summary>
/// <remarks>
/// The target is a real loopback Kestrel listener, not a stub <see cref="DelegatingHandler"/>
/// standing in for the primary handler: since .NET 7, <c>SocketsHttpHandler</c> injects
/// <c>traceparent</c> itself as part of the real send path, so a fake primary handler that
/// never delegates to it would never see the header at all.
/// </remarks>
public class HttpClientPropagationTests
{
    private static readonly ActivitySource TestSource = new("Decisya.ServiceDefaults.Tests.Http");

    [Fact]
    public async Task An_outgoing_call_made_inside_an_activity_carries_traceparent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        string? capturedTraceparent = null;

        var receiverBuilder = WebApplication.CreateSlimBuilder();
        receiverBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var receiver = receiverBuilder.Build();
        receiver.MapGet("/probe", (HttpContext context) =>
        {
            capturedTraceparent = context.Request.Headers.TryGetValue("traceparent", out var values)
                ? values.FirstOrDefault()
                : null;
            return Results.Ok();
        });
        await receiver.StartAsync(cancellationToken);
        var receiverAddress = receiver.Urls.First();

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Development",
        });
        builder.Configuration.AddInMemoryCollection(
        [
            new(DecisyaObservabilityOptions.UserIdHashKeyPath, Canaries.HashKey()),
        ]);
        builder.Services.AddHttpClient("outbound");
        builder.AddServiceDefaults();

        using var host = builder.Build();
        await host.StartAsync(cancellationToken);

        using (TestSource.StartActivity("outer"))
        {
            var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("outbound");
            using var response = await client.GetAsync($"{receiverAddress}/probe", cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        await host.StopAsync(cancellationToken);
        await receiver.StopAsync(cancellationToken);

        capturedTraceparent.Should().NotBeNullOrEmpty();
        capturedTraceparent.Should().MatchRegex("^00-[0-9a-f]{32}-[0-9a-f]{16}-0[01]$");
    }
}
