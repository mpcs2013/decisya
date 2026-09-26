using System.Text.Json;

namespace Decisya.Api.Tests;

/// <summary>
/// G4-15-03 (launch profile) and G4-15-04 (configuration files): the skeleton hardcodes no
/// OTLP endpoint, service name or observability secret, and binds only to localhost.
/// </summary>
public class LaunchProfileAndConfigurationTests
{
    [Fact]
    public void Every_launch_profile_is_a_plain_localhost_project_profile_with_no_OTEL_or_Decisya_variable()
    {
        var path = RepoPaths.Find(Path.Combine("src", "Decisya.Api", "Properties", "launchSettings.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        foreach (var profile in document.RootElement.GetProperty("profiles").EnumerateObject())
        {
            var value = profile.Value;
            value.GetProperty("commandName").GetString().Should().Be("Project");
            value.TryGetProperty("executablePath", out _).Should().BeFalse();
            value.TryGetProperty("commandLineArgs", out _).Should().BeFalse();
            value.TryGetProperty("workingDirectory", out _).Should().BeFalse();

            var applicationUrl = value.GetProperty("applicationUrl").GetString();
            applicationUrl.Should().NotBeNull();
            foreach (var url in applicationUrl!.Split(';'))
            {
                new Uri(url).Host.Should().Be("localhost");
            }

            if (value.TryGetProperty("environmentVariables", out var environmentVariables))
            {
                var names = environmentVariables.EnumerateObject().Select(p => p.Name).ToArray();
                names.Should().BeEquivalentTo(["ASPNETCORE_ENVIRONMENT"]);
            }
        }
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    public void No_appsettings_file_carries_an_observability_or_console_formatter_override(string fileName)
    {
        var path = RepoPaths.Find(Path.Combine("src", "Decisya.Api", fileName));
        var content = File.ReadAllText(path);

        content.Should().NotContain("Decisya:Observability");
        content.Should().NotContain("Decisya__Observability");
        content.Should().NotContain("Logging:Console:FormatterName");
        content.Should().NotContain("Logging:Console:FormatterOptions");
        content.Should().NotContain("Logging:OpenTelemetry");
        content.Should().NotContain("OTEL_");
    }

    [Fact]
    public void AllowedHosts_is_either_absent_or_the_wildcard()
    {
        var path = RepoPaths.Find(Path.Combine("src", "Decisya.Api", "appsettings.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        if (document.RootElement.TryGetProperty("AllowedHosts", out var allowedHosts))
        {
            allowedHosts.GetString().Should().Be("*");
        }
    }
}
