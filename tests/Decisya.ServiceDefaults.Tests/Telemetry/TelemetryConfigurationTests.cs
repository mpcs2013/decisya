using Decisya.ServiceDefaults.Telemetry;

namespace Decisya.ServiceDefaults.Tests.Telemetry;

/// <summary>
/// G4-15-30 (naming) and G4-15-29 (spans stay minimal): the wildcard carries the trailing
/// dot, and the source file contains none of the span-enrichment or experimental switches
/// the G3 threat model forbids.
/// </summary>
public class TelemetryConfigurationTests
{
    [Fact]
    public void The_source_wildcard_is_Decisya_dot_star()
    {
        DecisyaTelemetry.SourceWildcard.Should().Be("Decisya.*");
        DecisyaTelemetry.SourcePrefix.Should().Be("Decisya.");
    }

    [Fact]
    public void Extensions_cs_adds_no_span_enrichment_or_experimental_switch()
    {
        var path = RepoPaths.Find(System.IO.Path.Combine("src", "Decisya.ServiceDefaults", "Extensions.cs"));
        var content = System.IO.File.ReadAllText(path);

        content.Should().NotContain("EnrichWithHttpRequest");
        content.Should().NotContain("EnrichWithHttpResponse");
        content.Should().NotContain("EnrichWithException");
        content.Should().NotContain("RecordException");
        content.Should().NotContain("OTEL_DOTNET_EXPERIMENTAL_");
        content.Should().NotContain("DisableUriRedaction");
        content.Should().NotContain("DisableUrlQueryRedaction");
        // T-22 / G4-15-30: the template's health-path exclusion filter is gone, and no other
        // tracing filter replaces it — the Done-when is a trace of a health call.
        content.Should().NotContain(".Filter =");
    }
}
