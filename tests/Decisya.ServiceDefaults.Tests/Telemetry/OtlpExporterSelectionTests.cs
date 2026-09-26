using Decisya.ServiceDefaults.Telemetry;
using Microsoft.Extensions.Configuration;

namespace Decisya.ServiceDefaults.Tests.Telemetry;

/// <summary>
/// Story 1, scenarios 1 and 2: the OTLP exporter activates only when the AppHost supplies
/// a non-blank endpoint, and no exporter is wired (and nothing throws) otherwise.
/// </summary>
public class OtlpExporterSelectionTests
{
    [Fact]
    public void Is_enabled_when_the_endpoint_is_set()
    {
        var configuration = Build(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4318"));

        OtlpExporterSelection.IsEnabled(configuration).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Is_disabled_when_the_endpoint_is_blank(string value)
    {
        var configuration = Build(("OTEL_EXPORTER_OTLP_ENDPOINT", value));

        OtlpExporterSelection.IsEnabled(configuration).Should().BeFalse();
    }

    [Fact]
    public void Is_disabled_when_the_endpoint_is_absent()
    {
        var configuration = Build();

        OtlpExporterSelection.IsEnabled(configuration).Should().BeFalse();
    }

    private static IConfiguration Build(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}
