using System.Diagnostics;
using Decisya.ServiceDefaults.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Decisya.Api.Tests;

/// <summary>
/// Story 5 scenario 1's automated proxy: a call to <c>/alive</c> produces a completed
/// server span carrying the host's service, without needing the Aspire dashboard or a
/// network OTLP receiver (G2 layer 3; the health-trace filter removal, G4-15-30).
/// </summary>
public class TracingTests
{
    [Fact]
    public async Task A_call_to_alive_produces_a_completed_server_span()
    {
        var sink = new List<Activity>();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            [
                new(DecisyaObservabilityOptions.UserIdHashKeyPath, Canaries.HashKey()),
            ]));
            builder.ConfigureTestServices(services =>
                services.ConfigureOpenTelemetryTracerProvider((_, tracerBuilder) =>
                    tracerBuilder.AddProcessor(new CollectingActivityProcessor(sink))));
        });

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/alive");
        request.Headers.Add("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TracerProvider>().ForceFlush();

        sink.Should().Contain(a =>
            a.Kind == ActivityKind.Server
            && a.TraceId.ToHexString() == "0af7651916cd43dd8448eb211c80319c");
    }

    private sealed class CollectingActivityProcessor(List<Activity> sink) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data)
        {
            lock (sink)
            {
                sink.Add(data);
            }
        }
    }
}
