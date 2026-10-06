using Decisya.ServiceDefaults.Logging;
using Decisya.ServiceDefaults.Production;
using Decisya.ServiceDefaults.Telemetry;
using Decisya.SharedKernel.Time;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

// Adds common Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        // #120 (D6): file secrets and the production hosting checks. Both are no-ops in
        // Development. The secret source goes first so every later reader sees its values.
        builder.AddSecretFiles();
        builder.AddProductionHosting();

        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureDecisyaLogging();

        // #120 (T-22, T120-02): outside Development, a request that arrived on the management
        // local port (the health probe) produces no span. The test is the local port, never a
        // header: a client controls its Host header, so a header test would let it hide its
        // own requests from tracing (or, for health gating, reach the checks).
        builder.Services.AddOptions<AspNetCoreTraceInstrumentationOptions>()
            .Configure<IConfiguration, IHostEnvironment>(static (options, configuration, environment) =>
            {
                if (!environment.IsDevelopment())
                {
                    var managementPort = ManagementPort.Resolve(configuration);
                    options.Filter = context => context.Connection.LocalPort != managementPort;
                }
            });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    // Every module's Meter("Decisya.<Module>") is picked up without a
                    // change here. The prefix constant carries the trailing dot, so a
                    // meter called "DecisyaFoo" does not match.
                    .AddMeter(DecisyaTelemetry.SourceWildcard);
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(DecisyaTelemetry.SourceWildcard)
                    // The probe filter is set through AspNetCoreTraceInstrumentationOptions
                    // above, outside Development only; in Development a trace of a health
                    // call is exactly what issue #15 has to show.
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    /// <summary>
    /// Opt-in: makes this process treat every inbound request as the root of a new trace
    /// instead of continuing the caller's trace (issue #27, finding B-01).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>OpenTelemetry.Instrumentation.AspNetCore</c> extracts the inbound <c>traceparent</c>
    /// and <c>baggage</c> through the global <c>Propagators.DefaultTextMapPropagator</c>.
    /// This method replaces that propagator with a <see cref="TraceContextPropagator"/>.
    /// It is <b>process-wide and static</b>: it affects every instrumentation in the process,
    /// not just this host, and it drops <c>baggage</c> extraction as well as trace-context
    /// extraction on the inbound path.
    /// </para>
    /// <para>
    /// Only the BFF calls it. The BFF is the edge: its caller is a browser, which is
    /// untrusted, and a browser-supplied trace id or baggage must not parent the BFF's
    /// server span or flow downstream. The Api sits behind the BFF and must keep normal
    /// propagation so the BFF-to-Api trace stays connected. Do not call it from a service
    /// that has trusted callers.
    /// </para>
    /// <para>
    /// Why not a narrower switch: the AspNetCore instrumentation options expose no setting
    /// to skip context extraction while keeping other behaviour, so replacing the
    /// propagator is the supported lever. Outbound requests are unaffected:
    /// <c>HttpClient</c> injects <c>traceparent</c> from the current activity through
    /// <c>DistributedContextPropagator</c>, which this call does not touch. Call it once
    /// at startup, before the first request.
    /// </para>
    /// </remarks>
    public static TBuilder UseInboundTraceRoots<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        Sdk.SetDefaultTextMapPropagator(new TraceContextPropagator());

        return builder;
    }

    /// <summary>
    /// Replaces the host's logging providers with exactly two sinks, both of which mask:
    /// the <c>decisya-json</c> stdout formatter and the OpenTelemetry provider behind
    /// <see cref="MaskingLogRecordProcessor"/>.
    /// </summary>
    private static TBuilder ConfigureDecisyaLogging<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        var services = builder.Services;

        // NodaTime supplies every log timestamp (platform invariant 4). Hosts never call
        // AddSystemClock again; tests replace IClock with a FakeClock after AddServiceDefaults.
        services.AddSystemClock();

        services.AddOptions<DecisyaObservabilityOptions>()
            .Bind(builder.Configuration.GetSection(DecisyaObservabilityOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<UserIdHashKeyProvisioner>();
        services.AddSingleton<IPostConfigureOptions<DecisyaObservabilityOptions>>(
            static sp => sp.GetRequiredService<UserIdHashKeyProvisioner>());
        services.AddSingleton<IValidateOptions<DecisyaObservabilityOptions>, DecisyaObservabilityOptionsValidator>();
        services.AddSingleton<UserIdHasher>();
        services.AddSingleton<ILogEnrichmentContext, LogEnrichmentContext>();
        services.AddSingleton<SensitiveDataMaskingProcessor>();
        services.AddHostedService<EphemeralUserIdHashKeyWarning>();

        // Drops the Console (simple formatter), Debug, EventSource and EventLog providers
        // that the host builder adds. After this, every sink that exists masks.
        builder.Logging.ClearProviders();

        builder.Logging.AddConsole();
        builder.Logging.AddConsoleFormatter<DecisyaJsonConsoleFormatter, DecisyaJsonConsoleFormatterOptions>();
        // PostConfigure, not Configure: `Logging:Console:FormatterName` in configuration
        // must not be able to switch stdout back to an unmasked formatter.
        services.PostConfigure<ConsoleLoggerOptions>(
            static options => options.FormatterName = DecisyaJsonConsoleFormatter.FormatterName);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = false;
            logging.AddProcessor(static sp => new MaskingLogRecordProcessor(
                sp.GetRequiredService<SensitiveDataMaskingProcessor>(),
                sp.GetRequiredService<ILogEnrichmentContext>()));
        });
        // The OTLP exporter reads scopes straight from the scope provider, where a
        // processor cannot rewrite them, so raw scope values would bypass masking
        // entirely. Tenant and user reach OTLP through ILogEnrichmentContext instead.
        // PostConfigure so neither `Logging:OpenTelemetry` configuration nor a later
        // Configure<OpenTelemetryLoggerOptions> can turn it back on.
        services.PostConfigure<OpenTelemetryLoggerOptions>(static options =>
        {
            options.IncludeScopes = false;
            options.IncludeFormattedMessage = true;
        });

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        if (OtlpExporterSelection.IsEnabled(builder.Configuration))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // No vendor exporter is wired here, and none ever will be: NFR-12 keeps telemetry
        // on OTLP only, and the architecture tests fail the build on a vendor namespace.
        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Outside Development nothing is mapped here (#120, D6): the health endpoints are served
        // on the management port by ManagementHealthStartupFilter, which AddServiceDefaults
        // registers, for requests that arrived on that local port only. Adding the endpoints
        // to the application pipeline would expose them on the port Caddy routes to.
        if (app.Environment.IsDevelopment())
        {
            // All health checks must pass for app to be considered ready to accept traffic after starting.
            // AllowAnonymous: once a host adds a fallback policy that requires an authenticated
            // user (issue #20), these probes must stay reachable without a token. Development only.
            app.MapHealthChecks(HealthEndpointPath).AllowAnonymous();

            // Only health checks tagged with the "live" tag must pass for app to be considered alive
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            }).AllowAnonymous();
        }

        return app;
    }
}
