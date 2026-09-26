using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Decisya.AppHost.Tests;

/// <summary>
/// Story 5 (the Done-when) and G4-15-06: starts the real AppHost — DCP, the dashboard and
/// its OTLP collector included — and proves the resource wiring an operator would see on
/// the dashboard. Runs on Marco's host only (ADR-0010): DCP and the Aspire CLI bundle are
/// not available in the sandbox or in CI (F-5).
/// </summary>
[Trait("Category", "AppHost")]
public class AppHostResourceTests
{
    private const string KnownTraceId = "0af7651916cd43dd8448eb211c80319c";
    private const string KnownParentSpanId = "b7ad6b7169203331";
    private const string KnownTraceparent = $"00-{KnownTraceId}-{KnownParentSpanId}-01";
    private const string ResourceName = "decisya-api";

    [Fact]
    public async Task The_AppHost_injects_OTLP_configuration_and_a_health_call_produces_a_correlated_trace_and_log_line()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Decisya_AppHost>(cancellationToken);
        await using var app = await appHost.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);

        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        await notifications.WaitForResourceAsync(ResourceName, KnownResourceStates.Running, cancellationToken);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = model.Resources.OfType<IResourceWithEnvironment>().Single(r => r.Name == ResourceName);
        // Aspire.Hosting.Testing 13.5.4 marks this extension obsolete in favour of
        // ExecutionConfigurationBuilder, a divergence from the G2/G3 design text (which named
        // "the GetEnvironmentVariableValuesAsync testing extension" directly). It is still
        // functional, only advisory-deprecated, and is kept here rather than adopting an
        // unreviewed replacement API; reported for G6.
#pragma warning disable CS0618
        var variables = await resource.GetEnvironmentVariableValuesAsync(DistributedApplicationOperation.Run);
#pragma warning restore CS0618

        variables.Should().ContainKey("OTEL_EXPORTER_OTLP_ENDPOINT");
        // GetEnvironmentVariableValuesAsync (even called with DistributedApplicationOperation.Run)
        // returns OTEL_SERVICE_NAME as DCP's own unresolved annotation-template string
        // (observed: "{{- index .Annotations \"otel-service-name\" -}}"), not the value DCP
        // actually substitutes into the started process's real environment. That final
        // substitution is a DCP-side step this .NET-side testing API does not surface, a
        // divergence from the G2/G3 design text ("the resource's environment... holds
        // OTEL_SERVICE_NAME=decisya-api"). Presence is verified here; the resolved value is
        // Marco's manual dashboard check (docs/architecture/apphost-servicedefaults.md,
        // "What Marco checks manually on the host"). Reported for G6.
        variables.Should().ContainKey("OTEL_SERVICE_NAME");
        // G4-15-06: presence only, never the header's value, so the collector's API key
        // never lands in a test log or assertion message.
        variables.Should().ContainKey("OTEL_EXPORTER_OTLP_HEADERS");
        variables.Should().NotContainKey("Decisya__Observability__UserIdHashKey");

        // Subscribed before the call is made: WatchAsync streams log lines live from the
        // point of subscription and does not replay history, so watching only after the
        // response comes back would race the very lines this assertion looks for.
        var logLineTask = WaitForCorrelatedLogLineAsync(app, resource, cancellationToken);

        using var client = app.CreateHttpClient(ResourceName);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/alive");
        request.Headers.Add("traceparent", KnownTraceparent);

        using var response = await client.SendAsync(request, cancellationToken);
        response.IsSuccessStatusCode.Should().BeTrue();

        var logLine = await logLineTask;
        logLine.Should().NotBeNull("the decisya-api console stream should carry a JSON line for this request within 30s");

        await app.StopAsync(cancellationToken);
    }

    private static async Task<string?> WaitForCorrelatedLogLineAsync(
        DistributedApplication app, IResourceWithEnvironment resource, CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var loggerService = app.Services.GetRequiredService<ResourceLoggerService>();

        try
        {
            await foreach (var batch in loggerService.WatchAsync(resource).WithCancellation(linked.Token))
            {
                foreach (var line in batch)
                {
                    if (TryMatchesKnownTrace(line.Content))
                    {
                        return line.Content;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            return null;
        }

        return null;
    }

    private static bool TryMatchesKnownTrace(string line)
    {
        // A substring search, not a strict JsonDocument.Parse: the resource logger service
        // prefixes each captured stdout line with its own sequence number and timestamp
        // (observed: "9: 2026-...Z {json}"), so line.Content is not always bare JSON. The
        // trace id appearing anywhere in the line is exactly the evidence Story 5 scenario 3
        // asks for (the stdout line for the request carries the matching trace_id).
        return line.Contains(KnownTraceId, StringComparison.OrdinalIgnoreCase)
            && line.Contains("\"trace_id\"", StringComparison.Ordinal);
    }
}
