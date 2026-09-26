using System.Text.Json;

namespace Decisya.Api.Tests;

/// <summary>
/// NFR-12: no vendor telemetry exporter or observability SDK ever reaches
/// <c>Decisya.Api</c>'s published dependency closure.
/// </summary>
public class PackageClosureTests
{
    private static readonly string[] DeniedPrefixes =
    [
        "Microsoft.ApplicationInsights",
        "Azure.Monitor.",
        "Datadog.",
        "NewRelic.",
        "Elastic.Apm",
        "Sentry",
        "Honeycomb.",
        "Dynatrace.",
        "Splunk",
    ];

    [Fact]
    public void Decisya_Api_deps_json_lists_no_vendor_observability_package()
    {
        var depsPath = Path.Combine(AppContext.BaseDirectory, "Decisya.Api.deps.json");
        File.Exists(depsPath).Should().BeTrue($"Microsoft.AspNetCore.Mvc.Testing should have copied {depsPath}");

        using var document = JsonDocument.Parse(File.ReadAllText(depsPath));
        var libraryNames = document.RootElement.GetProperty("libraries").EnumerateObject()
            .Select(p => p.Name.Split('/')[0])
            .ToArray();

        foreach (var name in libraryNames)
        {
            if (name.StartsWith("OpenTelemetry.Exporter.", StringComparison.Ordinal))
            {
                name.Should().Be("OpenTelemetry.Exporter.OpenTelemetryProtocol");
                continue;
            }

            foreach (var denied in DeniedPrefixes)
            {
                name.Should().NotStartWith(denied);
            }
        }
    }
}
