using Decisya.ServiceDefaults.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;

namespace Decisya.ServiceDefaults.Tests.Logging;

/// <summary>
/// Wires a real generic-host <see cref="IHostApplicationBuilder"/> through
/// <c>AddServiceDefaults</c> and inspects the resulting DI container, without needing a
/// full ASP.NET Core web host (health endpoints and the real two-provider count on the
/// actual <c>Decisya.Api</c> host are covered separately in Decisya.Api.Tests).
/// </summary>
public class ServiceDefaultsLoggingWiringTests
{
    [Fact]
    public void Exactly_two_logger_providers_are_registered()
    {
        using var host = BuildHost("Development", withHashKey: true);

        var providers = host.Services.GetServices<ILoggerProvider>().ToArray();

        providers.Should().HaveCount(2);
        providers.Should().ContainSingle(p => p is ConsoleLoggerProvider);
        providers.Should().ContainSingle(p => p.GetType().Name == "OpenTelemetryLoggerProvider");
    }

    [Theory]
    [InlineData("simple")]
    [InlineData("json")]
    [InlineData("systemd")]
    public void The_console_formatter_stays_pinned_to_decisya_json_regardless_of_configuration(string requestedFormatter)
    {
        using var host = BuildHost("Development", withHashKey: true, ("Logging:Console:FormatterName", requestedFormatter));

        var options = host.Services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>();
        options.CurrentValue.FormatterName.Should().Be(DecisyaJsonConsoleFormatter.FormatterName);

        var formatters = host.Services.GetServices<ConsoleFormatter>()
            .Where(f => f.Name == DecisyaJsonConsoleFormatter.FormatterName)
            .ToArray();
        formatters.Should().ContainSingle();
        formatters[0].Should().BeOfType<DecisyaJsonConsoleFormatter>();
    }

    [Fact]
    public void A_configuration_override_cannot_turn_OTLP_log_scopes_back_on()
    {
        using var host = BuildHost(
            "Development",
            withHashKey: true,
            ("Logging:OpenTelemetry:IncludeScopes", "true"));

        var options = host.Services.GetRequiredService<IOptions<OpenTelemetryLoggerOptions>>();

        options.Value.IncludeScopes.Should().BeFalse();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Test")]
    public void The_host_fails_to_start_outside_Development_without_a_hash_key(string environmentName)
    {
        using var host = BuildHost(environmentName, withHashKey: false);

        var act = () => host.Services.GetRequiredService<IOptions<DecisyaObservabilityOptions>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain(DecisyaObservabilityOptions.UserIdHashKeyPath);
    }

    [Fact]
    public void Development_generates_a_per_process_key_when_none_is_configured()
    {
        using var host = BuildHost("Development", withHashKey: false);

        var options = host.Services.GetRequiredService<IOptions<DecisyaObservabilityOptions>>();

        options.Value.UserIdHashKey.Should().NotBeNullOrWhiteSpace();
        DecisyaObservabilityOptionsValidator.IsAcceptableKey(options.Value.UserIdHashKey).Should().BeTrue();
    }

    [Fact]
    public void The_masking_core_and_enrichment_context_are_registered_as_singletons()
    {
        using var host = BuildHost("Development", withHashKey: true);

        host.Services.GetRequiredService<SensitiveDataMaskingProcessor>().Should().NotBeNull();
        host.Services.GetRequiredService<ILogEnrichmentContext>().Should().NotBeNull();
    }

    private static IHost BuildHost(string environmentName, bool withHashKey, params (string Key, string Value)[] extra)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environmentName,
        });

        var values = new List<KeyValuePair<string, string?>>();
        if (withHashKey)
        {
            values.Add(new(DecisyaObservabilityOptions.UserIdHashKeyPath, Canaries.HashKey()));
        }

        values.AddRange(extra.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)));
        builder.Configuration.AddInMemoryCollection(values);

        builder.AddServiceDefaults();

        return builder.Build();
    }
}
