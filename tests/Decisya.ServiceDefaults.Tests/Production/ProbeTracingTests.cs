using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Decisya.ServiceDefaults.Tests.Production;

/// <summary>
/// Issue #120, T-22 and T120-02: outside Development a request that arrived on the management
/// port produces no span. The positive control proves the processor does see a span for a
/// request on the application port, so "no span" cannot mean "the pipeline records nothing".
/// </summary>
[Collection(RealAspNetCoreHostCollectionDefinition.Name)]
public class ProbeTracingTests
{
    [Fact]
    public async Task A_management_port_request_produces_no_span_and_an_application_port_request_does()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var spans = new ConcurrentQueue<string>();
        await using var host = await StartAsync(Environments.Production, spans, cancellationToken);

        using (await ProductionTestHost.GetAsync(host.ManagementUri, "/health", host: null, cancellationToken))
        {
        }

        using (await ProductionTestHost.GetAsync(host.ManagementUri, "/alive", host: null, cancellationToken))
        {
        }

        // The positive control goes last: once its span is seen, any earlier span would be too.
        using (await ProductionTestHost.GetAsync(host.AppUri, "/ping", ProductionTestHost.AllowedHost, cancellationToken))
        {
        }

        await WaitForAsync(() => spans.Any(static s => s.Contains("/ping", StringComparison.Ordinal)), cancellationToken);

        spans.Should().Contain(static s => s.Contains("/ping", StringComparison.Ordinal), "positive control");
        spans.Should().NotContain(static s => s.Contains("/health", StringComparison.Ordinal) || s.Contains("/alive", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Host_header_naming_the_management_port_does_not_hide_an_application_port_request()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var spans = new ConcurrentQueue<string>();
        await using var host = await StartAsync(Environments.Production, spans, cancellationToken);

        // A client controls its Host header, so the header must never decide what is traced.
        var managementHost = $"{ProductionTestHost.AllowedHost}:{host.ManagementUri.Port}";
        using (await ProductionTestHost.GetAsync(host.AppUri, "/ping", managementHost, cancellationToken))
        {
        }

        await WaitForAsync(() => !spans.IsEmpty, cancellationToken);

        spans.Should().Contain(static s => s.Contains("/ping", StringComparison.Ordinal));
    }

    [Fact]
    public async Task In_Development_a_health_request_is_traced()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var spans = new ConcurrentQueue<string>();
        await using var host = await StartAsync(Environments.Development, spans, cancellationToken);

        using (await ProductionTestHost.GetAsync(host.AppUri, "/health", ProductionTestHost.AllowedHost, cancellationToken))
        {
        }

        await WaitForAsync(() => !spans.IsEmpty, cancellationToken);

        spans.Should().Contain(static s => s.Contains("/health", StringComparison.Ordinal));
    }

    private static Task<ProductionTestHost> StartAsync(
        string environment, ConcurrentQueue<string> spans, CancellationToken cancellationToken) =>
        ProductionTestHost.StartAsync(
            environment,
            configureBuilder: builder => builder.Services.AddOpenTelemetry()
                .WithTracing(tracing => tracing.AddProcessor(new CollectingProcessor(spans))),
            cancellationToken: cancellationToken);

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(25, cancellationToken);
        }
    }

    private sealed class CollectingProcessor(ConcurrentQueue<string> spans) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data)
        {
            // The ASP.NET Core server span carries the path in url.path.
            spans.Enqueue($"{data.DisplayName} {data.GetTagItem("url.path")}");
        }
    }
}
