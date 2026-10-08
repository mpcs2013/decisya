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

    // Issue #121 (G1 Q6, G2 D3): Authentication__RequireAdminMfa is a non-secret feature flag, set to
    // "false" on decisya-api in run mode only (the dev realm has no OTP; the Api fails closed if it
    // is false outside Development). It carries no credential, so a literal is acceptable.
    private static readonly string[] AllowedLiteralEnvironmentKeys = ["KC_DB", "KC_DB_USERNAME", "Authentication__RequireAdminMfa"];

    // Literal values pinned per key: Authentication__RequireAdminMfa may only be "false" (#121).
    private static readonly Dictionary<string, string> PinnedLiteralEnvironmentValues = new()
    {
        ["Authentication__RequireAdminMfa"] = "false",
    };

    private static readonly string[] SecretParameterNames = ["dev-user-password", "bff-client-secret", "keycloak-db-password", "tenancy-db-password", "entitlements-db-password"];

    [Fact]
    public void AppHost_cs_passes_secrets_only_through_parameters()
    {
        var appHostCs = RepoPaths.Find(Path.Combine("src", "Decisya.AppHost", "AppHost.cs"));
        var content = File.ReadAllText(appHostCs);

        content.Should().Contain("AddProject<Projects.Decisya_Api>(\"decisya-api\", launchProfileName: \"https\")");

        // Issue #19 (G2 AppHost section): decisya-bff resolves https://decisya-api through
        // service discovery, so it must reference the api resource.
        content.Should().MatchRegex(
            "AddProject<Projects\\.Decisya_Bff>\\(\"decisya-bff\"[\\s\\S]*?\\.WithReference\\(api\\)",
            "decisya-bff should reference the api project for service discovery (#19)");

        var literalKeys = LiteralEnvironmentCall.Matches(content).Select(m => m.Groups[1].Value).ToList();
        literalKeys.Should().NotBeEmpty("AppHost.cs should wire KC_DB and KC_DB_USERNAME as literals");

        foreach (var key in literalKeys)
        {
            AllowedLiteralEnvironmentKeys.Should().Contain(
                key, $"'{key}' is a literal WithEnvironment value; only {string.Join(", ", AllowedLiteralEnvironmentKeys)} may be");
        }

        foreach (var match in LiteralEnvironmentCall.Matches(content).Where(m => PinnedLiteralEnvironmentValues.ContainsKey(m.Groups[1].Value)))
        {
            var key = match.Groups[1].Value;
            match.Groups[2].Value.Should().Be(
                PinnedLiteralEnvironmentValues[key], $"'{key}' may only be set to the literal \"{PinnedLiteralEnvironmentValues[key]}\" (#121)");
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
    /// Marco's persistent containers. Issue #18 (0.06 BFF) adds Redis, the BFF's session
    /// ticket store, as a third persistent, fixed-name container under the same override.
    /// </summary>
    [Fact]
    public void AppHost_cs_marks_postgres_keycloak_and_redis_persistent_with_fixed_names_and_a_test_time_override()
    {
        var appHostCs = RepoPaths.Find(Path.Combine("src", "Decisya.AppHost", "AppHost.cs"));
        var content = File.ReadAllText(appHostCs);

        Regex.Count(content, "WithLifetime\\(ContainerLifetime\\.Persistent\\)").Should().Be(
            3, "postgres, keycloak and redis should all be marked ContainerLifetime.Persistent");
        content.Should().Contain("WithContainerName(\"decisya-postgres\")");
        content.Should().Contain("WithContainerName(\"decisya-keycloak\")");
        content.Should().Contain("WithContainerName(\"decisya-redis\")");

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

    /// <summary>
    /// Issue #20 (0.08, G2): the API resource gets its JWT authority from the same Keycloak
    /// endpoint expression the BFF uses, so discovery's issuer matches every token's "iss",
    /// and waits for Keycloak to be ready. There is deliberately no <c>.WithReference(keycloak)</c>
    /// — the API is a bearer-only resource server that needs no service-discovery reference or
    /// secret to Keycloak (S-3).
    /// </summary>
    [Fact]
    public void AppHost_cs_gives_decisya_api_its_jwt_authority_from_an_expression_and_waits_for_keycloak_without_referencing_it()
    {
        var appHostCs = RepoPaths.Find(Path.Combine("src", "Decisya.AppHost", "AppHost.cs"));
        var content = File.ReadAllText(appHostCs);

        var apiBlockMatch = Regex.Match(
            content,
            "var api = builder\\.AddProject<Projects\\.Decisya_Api>\\(\"decisya-api\"[\\s\\S]*?;\\r?\\n",
            RegexOptions.Multiline);
        apiBlockMatch.Success.Should().BeTrue("AppHost.cs should declare the decisya-api resource as a single statement");
        var apiBlock = apiBlockMatch.Value;

        apiBlock.Should().MatchRegex(
            "\\.WithEnvironment\\(\"Api__Jwt__Authority\",\\s*ReferenceExpression\\.Create\\(",
            "decisya-api should set Api__Jwt__Authority from a ReferenceExpression, not a literal");
        apiBlock.Should().Contain(".WaitFor(keycloak)", "decisya-api should wait for Keycloak to be ready before accepting bearer tokens");
        apiBlock.Should().NotContain(".WithReference(keycloak)", "the API is a bearer-only resource server (S-3); no service-discovery reference to Keycloak");
    }

    /// <summary>
    /// Issue #21 (0.09 Modules.Tenancy), G3 G4-21-05, T-12: the API must carry no owner
    /// credential at all. A static, text-level check (platform-dev's file boundary does not
    /// cover this test class; test-engineer adds it): <c>decisya-api</c>'s own declaration
    /// block sets <c>ConnectionStrings__tenancy</c> and no other <c>ConnectionStrings__*</c>
    /// key, and waits for <c>decisya-migrator</c> to finish before it ever opens that
    /// connection. The real, dynamic proof (starting the AppHost and reading the resource's
    /// actual environment) is <c>Decisya.AppHost.Tests.TenancyMigratorResourceTests</c>
    /// (Category=AppHost, needs Docker); this test runs in CI's plain unit step.
    /// </summary>
    [Fact]
    public void AppHost_cs_gives_decisya_api_only_the_tenancy_and_entitlements_connection_strings_and_waits_for_the_migrator()
    {
        var appHostCs = RepoPaths.Find(Path.Combine("src", "Decisya.AppHost", "AppHost.cs"));
        var content = File.ReadAllText(appHostCs);

        var apiBlockMatch = Regex.Match(
            content,
            "var api = builder\\.AddProject<Projects\\.Decisya_Api>\\(\"decisya-api\"[\\s\\S]*?;\\r?\\n",
            RegexOptions.Multiline);
        apiBlockMatch.Success.Should().BeTrue("AppHost.cs should declare the decisya-api resource as a single statement");
        var apiBlock = apiBlockMatch.Value;

        apiBlock.Should().MatchRegex(
            "\\.WithEnvironment\\(\"ConnectionStrings__tenancy\",\\s*ReferenceExpression\\.Create\\(",
            "decisya-api should get ConnectionStrings__tenancy from a ReferenceExpression, not a literal");
        apiBlock.Should().Contain("Username=decisya_tenancy", "decisya-api must connect only as the least-privilege decisya_tenancy role");

        var connectionStringKeyOccurrences = Regex.Matches(apiBlock, "WithEnvironment\\(\"(ConnectionStrings__[^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToList();
        apiBlock.Should().MatchRegex(
            "\\.WithEnvironment\\(\"ConnectionStrings__entitlements\",\\s*ReferenceExpression\\.Create\\(",
            "decisya-api should get ConnectionStrings__entitlements from a ReferenceExpression, not a literal (#23, G4-23-04)");
        apiBlock.Should().Contain("Username=decisya_entitlements", "decisya-api must connect to the entitlements schema only as the least-privilege decisya_entitlements role");

        connectionStringKeyOccurrences.Should().Equal(
            ["ConnectionStrings__tenancy", "ConnectionStrings__entitlements"],
            "decisya-api's own declaration should carry exactly the tenancy and entitlements ConnectionStrings__* keys, in that order (#23, G4-23-04)");

        apiBlock.Should().NotContain("Migrator__", "the API must never carry a migrator key (T-12)");
        apiBlock.Should().NotContain("Username=postgres", "the API must never carry an owner connection string (T-12)");

        apiBlock.Should().NotContain(".WithReference(postgres)", "an owner/superuser reference would inject ConnectionStrings__postgres alongside the least-privilege string (T-12)");
        apiBlock.Should().NotContain(".WithReference(decisyaDb)", "an owner/superuser reference would inject ConnectionStrings__decisya alongside the least-privilege string (T-12)");

        apiBlock.Should().Contain(".WaitForCompletion(migrator)", "decisya-api must wait for decisya-migrator to finish creating the schema and the role first");
    }

    /// <summary>
    /// Issue #23 (G3 G4-23-04, T-11, T-12): the migrator gets the entitlements role password
    /// only from the secret <c>entitlements-db-password</c> parameter, as
    /// <c>Migrator__EntitlementsRolePassword</c>, and gets no <c>ConnectionStrings__entitlements</c>
    /// (only the API connects as that role). The parameter is generated, persisted, at least 32
    /// characters and alphanumeric (<c>Special = false</c>), so it passes the migrator's
    /// <c>\A[A-Za-z0-9]{32,}\z</c> shape check.
    /// </summary>
    [Fact]
    public void AppHost_cs_gives_decisya_migrator_the_entitlements_role_password_and_no_entitlements_connection_string()
    {
        var appHostCs = RepoPaths.Find(Path.Combine("src", "Decisya.AppHost", "AppHost.cs"));
        var content = File.ReadAllText(appHostCs);

        var migratorBlockMatch = Regex.Match(
            content,
            "var migrator = builder\\.AddProject<Projects\\.Decisya_Infrastructure_Migrator>\\(\"decisya-migrator\"[\\s\\S]*?;\\r?\\n",
            RegexOptions.Multiline);
        migratorBlockMatch.Success.Should().BeTrue("AppHost.cs should declare the decisya-migrator resource as a single statement");
        var migratorBlock = migratorBlockMatch.Value;

        migratorBlock.Should().MatchRegex(
            "\\.WithEnvironment\\(\"Migrator__EntitlementsRolePassword\",\\s*entitlementsDbPassword\\)",
            "decisya-migrator should get Migrator__EntitlementsRolePassword from the entitlements-db-password parameter");
        migratorBlock.Should().MatchRegex(
            "\\.WithEnvironment\\(\"Migrator__TenancyRolePassword\",\\s*tenancyDbPassword\\)");
        migratorBlock.Should().NotContain("ConnectionStrings__entitlements");
        migratorBlock.Should().NotContain("ConnectionStrings__tenancy");

        content.Should().MatchRegex(
            "var entitlementsDbPassword = builder\\.AddParameter\\(\\s*\"entitlements-db-password\",\\s*new GenerateParameterDefault \\{ MinLength = 32, Special = false \\},\\s*secret: true,\\s*persist: true\\)",
            "entitlements-db-password should be secret, persisted, at least 32 characters and alphanumeric");
    }

    [Fact]
    public void No_mcp_configuration_file_exists_under_src_or_the_repo_root()
    {
        var root = RepoPaths.Find(string.Empty);
        File.Exists(Path.Combine(root, ".mcp.json")).Should().BeFalse();

        var srcRoot = RepoPaths.Find("src");
        ScanExclusions.EnumerateFiles(root, srcRoot, ".mcp.json").Should().BeEmpty();
    }
}
