using System.Diagnostics;
using Decisya.ServiceDefaults.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
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

    [Fact]
    public async Task A_query_canary_on_alive_never_reaches_a_span_tag_or_the_log_output()
    {
        // G4-15-29 (G6 review): whether url.query or url.full reach span tags depends on the
        // default redaction in OpenTelemetry.Instrumentation.AspNetCore, and on which tags
        // .NET's own Microsoft.AspNetCore activity sets. Both can change with a package
        // bump, so this is checked with a real request rather than accepted by reading code.
        var canary = Canaries.Unique("query-token");
        var activitySink = new List<Activity>();
        var logLines = new List<string>();

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            [
                new(DecisyaObservabilityOptions.UserIdHashKeyPath, Canaries.HashKey()),
            ]));
            builder.ConfigureTestServices(services =>
            {
                services.ConfigureOpenTelemetryTracerProvider((_, tracerBuilder) =>
                    tracerBuilder.AddProcessor(new CollectingActivityProcessor(activitySink)));
                services.AddSingleton<ILoggerProvider>(sp => new CapturingLoggerProvider(
                    sp.GetServices<ConsoleFormatter>().Single(f => f.Name == DecisyaJsonConsoleFormatter.FormatterName),
                    logLines));
            });
        });

        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/alive?token={canary}", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TracerProvider>().ForceFlush();

        activitySink.Should().NotBeEmpty();
        foreach (var activity in activitySink)
        {
            foreach (var tag in activity.TagObjects)
            {
                tag.Value?.ToString().Should().NotContain(canary, $"tag '{tag.Key}' must not carry the query canary");
            }
        }

        lock (logLines)
        {
            logLines.Should().NotContain(line => line.Contains(canary, StringComparison.Ordinal));
        }
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

    /// <summary>
    /// Resolves the real, DI-registered <c>decisya-json</c> <see cref="ConsoleFormatter"/>
    /// and calls it directly for each log entry, capturing the finished line without
    /// redirecting the process-wide <see cref="Console.Out"/> (which would race other tests
    /// running in a parallel xunit collection). Mirrors the G2 architecture note's
    /// "CapturingLoggerProvider" design.
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        internal readonly ConsoleFormatter Formatter;
        internal readonly List<string> Sink;
        internal IExternalScopeProvider? ScopeProvider;

        public CapturingLoggerProvider(ConsoleFormatter formatter, List<string> sink)
        {
            Formatter = formatter;
            Sink = sink;
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => ScopeProvider = scopeProvider;

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider provider, string categoryName) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var entry = new LogEntry<TState>(logLevel, categoryName, eventId, state, exception, formatter);
                using var writer = new StringWriter();
                provider.Formatter.Write(in entry, provider.ScopeProvider, writer);

                lock (provider.Sink)
                {
                    provider.Sink.Add(writer.ToString());
                }
            }
        }
    }
}
