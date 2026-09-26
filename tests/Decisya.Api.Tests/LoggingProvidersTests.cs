using Decisya.ServiceDefaults.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Decisya.Api.Tests;

/// <summary>
/// G4-15-07 (exactly two providers on the real host) and G4-15-08 (the effective formatter
/// resists a configuration override), pinned against the actual <c>Decisya.Api</c> host
/// that #20 will extend, not a stand-in.
/// </summary>
public class LoggingProvidersTests
{
    [Fact]
    public void Exactly_one_console_and_one_OpenTelemetry_logger_provider_are_registered()
    {
        using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();

        var providers = scope.ServiceProvider.GetServices<ILoggerProvider>().ToArray();

        providers.Should().ContainSingle(p => p is ConsoleLoggerProvider);
        providers.Should().ContainSingle(p => p.GetType().Name == "OpenTelemetryLoggerProvider");
        providers.Should().HaveCount(2, "WebApplicationFactory adds no extra provider of its own in this test");
    }

    [Theory]
    [InlineData("simple")]
    [InlineData("json")]
    [InlineData("systemd")]
    public void The_console_formatter_never_switches_away_from_decisya_json(string requestedFormatterName)
    {
        using var factory = CreateFactory(("Logging:Console:FormatterName", requestedFormatterName));
        using var scope = factory.Services.CreateScope();

        var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>();
        options.CurrentValue.FormatterName.Should().Be(DecisyaJsonConsoleFormatter.FormatterName);

        var formatters = scope.ServiceProvider.GetServices<ConsoleFormatter>()
            .Where(f => f.Name == DecisyaJsonConsoleFormatter.FormatterName)
            .ToArray();
        formatters.Should().ContainSingle();
        formatters[0].Should().BeOfType<DecisyaJsonConsoleFormatter>();
    }

    private static WebApplicationFactory<Program> CreateFactory(params (string Key, string Value)[] extraConfiguration) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var values = new List<KeyValuePair<string, string?>>
                {
                    new(DecisyaObservabilityOptions.UserIdHashKeyPath, Canaries.HashKey()),
                };
                values.AddRange(extraConfiguration.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)));
                config.AddInMemoryCollection(values);
            });
        });
}
