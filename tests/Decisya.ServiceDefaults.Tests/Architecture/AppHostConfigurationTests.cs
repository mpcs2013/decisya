using System.Text.RegularExpressions;

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

    /// <summary>
    /// Issue #17 (0.05, G2): AppHost.cs now wires Postgres and Keycloak, which need
    /// non-secret literal environment values (KC_DB, KC_DB_USERNAME). This replaces the
    /// old "no WithEnvironment at all" rule with a narrower one: every literal-valued
    /// WithEnvironment call names an allow-listed non-secret key, and every secret
    /// reaches the containers only through a `secret: true` parameter.
    /// </summary>
    private static readonly Regex LiteralEnvironmentCall = new(
        "WithEnvironment\\(\"([^\"]+)\",\\s*\"([^\"]*)\"\\)", RegexOptions.Compiled);

    private static readonly string[] AllowedLiteralEnvironmentKeys = ["KC_DB", "KC_DB_USERNAME"];

    private static readonly string[] SecretParameterNames = ["dev-user-password", "bff-client-secret", "keycloak-db-password"];

    [Fact]
    public void AppHost_cs_passes_secrets_only_through_parameters()
    {
        var appHostCs = RepoPaths.Find(Path.Combine("src", "Decisya.AppHost", "AppHost.cs"));
        var content = File.ReadAllText(appHostCs);

        content.Should().Contain("AddProject<Projects.Decisya_Api>(\"decisya-api\")");

        var literalKeys = LiteralEnvironmentCall.Matches(content).Select(m => m.Groups[1].Value).ToList();
        literalKeys.Should().NotBeEmpty("AppHost.cs should wire KC_DB and KC_DB_USERNAME as literals");

        foreach (var key in literalKeys)
        {
            AllowedLiteralEnvironmentKeys.Should().Contain(
                key, $"'{key}' is a literal WithEnvironment value; only {string.Join(", ", AllowedLiteralEnvironmentKeys)} may be");
        }

        foreach (var parameterName in SecretParameterNames)
        {
            content.Should().MatchRegex(
                $"AddParameter\\(\\s*\"{Regex.Escape(parameterName)}\"[\\s\\S]*?secret:\\s*true",
                $"'{parameterName}' should be added with secret: true");
        }

        // The only allowed "Parameters:" literal is the guard's own configuration-key
        // lookup; a `:default` suffix would mean a literal fallback secret in source.
        content.Should().NotContain(":default");
        var parametersLiteralOccurrences = Regex.Count(content, "\"Parameters:[^\"]*\"");
        parametersLiteralOccurrences.Should().Be(1, "only the RealmSecretRules guard's own key lookup should reference \"Parameters:...\"");
    }

    /// <summary>
    /// Marco's decision (2026-09-27, issue #17): a real dev run keeps postgres and keycloak
    /// as persistent, fixed-name containers (so a later `dotnet run` reuses them instead of
    /// starting a second writer against the same data volume), and a Category=AppHost test
    /// must be able to turn that off entirely — Decisya.AppHost.Tests' own
    /// TestAppHostIsolation passes the override rather than ever attaching to, or stopping,
    /// Marco's persistent containers.
    /// </summary>
    [Fact]
    public void AppHost_cs_marks_postgres_and_keycloak_persistent_with_fixed_names_and_a_test_time_override()
    {
        var appHostCs = RepoPaths.Find(Path.Combine("src", "Decisya.AppHost", "AppHost.cs"));
        var content = File.ReadAllText(appHostCs);

        Regex.Count(content, "WithLifetime\\(ContainerLifetime\\.Persistent\\)").Should().Be(
            2, "postgres and keycloak should both be marked ContainerLifetime.Persistent");
        content.Should().Contain("WithContainerName(\"decisya-postgres\")");
        content.Should().Contain("WithContainerName(\"decisya-keycloak\")");

        // The test path must be able to turn both off by configuration, never by editing
        // AppHost.cs per run.
        content.Should().Contain("AppHost:UseEphemeralContainers");
        content.Should().MatchRegex("if\\s*\\(\\s*!useEphemeralContainers\\s*\\)");

        // G6-04: ephemeral mode must refuse to run against the default dev volume, and must
        // require the override name to match TestAppHostIsolation's generated shape — the
        // one combination that reintroduced the 2026-09-26 volume-corruption incident.
        content.Should().MatchRegex("if\\s*\\(\\s*useEphemeralContainers\\s*\\)");
        content.Should().Contain("decisya-postgres-data");
        content.Should().Contain("decisya-apphosttests-[0-9a-f]{32}");
        Regex.Count(content, "throw new InvalidOperationException").Should().BeGreaterThanOrEqualTo(
            2, "ephemeral mode should refuse both the default-volume-name case and the wrong-shape case");
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
