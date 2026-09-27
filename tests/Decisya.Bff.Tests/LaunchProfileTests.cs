using System.Text.Json;

namespace Decisya.Bff.Tests;

/// <summary>Static checks NetArchTest cannot express (G2's "Static rules" table).</summary>
public class LaunchProfileTests
{
    [Fact]
    public void LaunchSettings_has_exactly_one_https_profile_at_localhost_7200_and_no_http_url()
    {
        var path = RepoPaths.Find(Path.Combine("src", "Decisya.Bff", "Properties", "launchSettings.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var profiles = document.RootElement.GetProperty("profiles").EnumerateObject().ToList();
        profiles.Should().HaveCount(1);
        profiles[0].Name.Should().Be("https");

        var applicationUrl = profiles[0].Value.GetProperty("applicationUrl").GetString();
        applicationUrl.Should().Be("https://localhost:7200");
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    public void No_appsettings_file_carries_a_ClientSecret_key(string fileName)
    {
        var path = RepoPaths.Find(Path.Combine("src", "Decisya.Bff", fileName));
        var content = File.ReadAllText(path);

        content.Should().NotContain("ClientSecret");
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    public void Microsoft_AspNetCore_stays_at_Warning_where_configured(string fileName)
    {
        var path = RepoPaths.Find(Path.Combine("src", "Decisya.Bff", fileName));
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        if (document.RootElement.TryGetProperty("Logging", out var logging)
            && logging.TryGetProperty("LogLevel", out var logLevel)
            && logLevel.TryGetProperty("Microsoft.AspNetCore", out var microsoftAspNetCore))
        {
            microsoftAspNetCore.GetString().Should().Be("Warning");
        }

        // S-1: the OIDC callback carries "code" and "state" in the query string under
        // ResponseMode=query, so the hosting request-log category must never be elevated.
        var content = File.ReadAllText(path);
        content.Should().NotContain("Microsoft.AspNetCore.Hosting");
    }
}
