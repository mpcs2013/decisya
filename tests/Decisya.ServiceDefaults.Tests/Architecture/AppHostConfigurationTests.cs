namespace Decisya.ServiceDefaults.Tests.Architecture;

/// <summary>
/// G4-15-05: static checks over the AppHost's own files. These run in CI (they carry no
/// <c>Category=AppHost</c> trait and need neither DCP nor the Aspire CLI bundle), unlike
/// the real-AppHost tests in <c>Decisya.AppHost.Tests</c>.
/// </summary>
public class AppHostConfigurationTests
{
    private static readonly string[] ForbiddenTokens =
    [
        "ASPIRE_ALLOW_UNSECURED_TRANSPORT",
        "UNSECURED_ALLOW_ANONYMOUS",
        "AuthMode",
        "ASPIRE_DASHBOARD_MCP",
        "ASPIRE_DASHBOARD_AI",
    ];

    public static IEnumerable<object[]> AppHostFiles()
    {
        var root = RepoPaths.Find(Path.Combine("src", "Decisya.AppHost"));
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file);
            if (extension is ".json" or ".cs")
            {
                yield return [file];
            }
        }
    }

    [Theory]
    [MemberData(nameof(AppHostFiles))]
    public void No_AppHost_file_contains_an_unsecured_dashboard_setting(string filePath)
    {
        var content = File.ReadAllText(filePath);

        foreach (var token in ForbiddenTokens)
        {
            // L-2 (G6 review): case-insensitive. .NET configuration keys and Windows
            // environment variable names are case-insensitive, so a differently-cased
            // spelling (for example "aspire_allow_unsecured_transport") would otherwise
            // pass an ordinal check while still working at runtime.
            content.Should().NotContainEquivalentOf(token, $"'{token}' must never appear in {filePath}");
        }
    }

    [Fact]
    public void Every_url_in_launchSettings_binds_to_localhost()
    {
        var launchSettings = RepoPaths.Find(Path.Combine("src", "Decisya.AppHost", "Properties", "launchSettings.json"));
        var content = File.ReadAllText(launchSettings);

        content.Should().NotContain("0.0.0.0");
        content.Should().NotContain("://+");
        content.Should().NotContain("://*");
        content.Should().NotContain("[::]");
    }

    [Fact]
    public void AppHost_cs_adds_only_the_decisya_api_project_resource_with_no_secret_environment()
    {
        var appHostCs = RepoPaths.Find(Path.Combine("src", "Decisya.AppHost", "AppHost.cs"));
        var content = File.ReadAllText(appHostCs);

        content.Should().Contain("AddProject<Projects.Decisya_Api>(\"decisya-api\")");
        content.Should().NotContain("WithEnvironment");
    }

    [Fact]
    public void No_mcp_configuration_file_exists_under_src_or_the_repo_root()
    {
        var root = RepoPaths.Find(string.Empty);
        File.Exists(Path.Combine(root, ".mcp.json")).Should().BeFalse();

        var srcRoot = RepoPaths.Find("src");
        Directory.EnumerateFiles(srcRoot, ".mcp.json", SearchOption.AllDirectories).Should().BeEmpty();
    }
}
