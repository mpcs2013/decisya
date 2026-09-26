using Decisya.ServiceDefaults.Logging;
using Decisya.ServiceDefaults.Telemetry;
using Decisya.SharedKernel.Time;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using OpenTelemetry;
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
                    // No Filter: the health endpoints exist only in Development, nothing
                    // polls them, and a trace of a health call is exactly what issue #15
                    // has to show. Probe exposure and probe sampling in production are
                    // decided together by the deployment issue (0.17).
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

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

        // Adding health checks endpoints to applications in non-development environments has security implications.
        // See https://aka.ms/aspire/healthchecks for details before enabling these endpoints in non-development environments.
        if (app.Environment.IsDevelopment())
        {
            // All health checks must pass for app to be considered ready to accept traffic after starting
            app.MapHealthChecks(HealthEndpointPath);

            // Only health checks tagged with the "live" tag must pass for app to be considered alive
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });
        }

        return app;
    }
}
