using System.Text.Json;
using System.Text.RegularExpressions;
using Decisya.AppHost;

namespace Decisya.Identity.Tests;

/// <summary>
/// G4-17-01: static checks over the hand-curated realm export. These never need Docker and
/// run in CI's unit step (no trait), unlike the <c>Category=Integration</c> tests that read
/// the same file through a running Keycloak container.
/// </summary>
public class RealmExportFileTests
{
    private static readonly Regex PlaceholderToken = new(@"\$\{[^}]*\}", RegexOptions.Compiled);

    private static readonly string[] AllowedRealmRoles = ["tenant-user", "platform-admin"];

    private static readonly string[] ForbiddenKeyMaterialTokens =
    [
        "privateKey",
        "secretData",
        "credentialData",
        "hashedSaltedValue",
        "org.keycloak.keys.KeyProvider",
    ];

    private static string RealmFilePath { get; } =
        RepoPaths.Find(Path.Combine("deploy", "keycloak", "decisya-realm.json"));

    private static string ReadRealmFile() => File.ReadAllText(RealmFilePath);

    private static JsonDocument ParseRealmFile() => JsonDocument.Parse(ReadRealmFile());

    [Fact]
    public void The_realm_file_parses_as_JSON()
    {
        using var document = ParseRealmFile();

        document.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public void The_realm_file_contains_exactly_the_two_allowed_placeholders()
    {
        var content = ReadRealmFile();
        var tokens = PlaceholderToken.Matches(content).Select(m => m.Value).Distinct();

        tokens.Should().BeEquivalentTo(
        [
            RealmSecretRules.BffClientSecretPlaceholderLiteral,
            RealmSecretRules.DevUserPasswordPlaceholderLiteral,
        ]);
    }

    [Fact]
    public void The_decisya_bff_client_secret_is_exactly_the_client_secret_placeholder()
    {
        using var document = ParseRealmFile();
        var bff = FindClient(document, "decisya-bff");

        bff.GetProperty("secret").GetString().Should().Be(RealmSecretRules.BffClientSecretPlaceholderLiteral);
    }

    [Fact]
    public void The_client_secret_field_appears_only_on_the_decisya_bff_client()
    {
        using var document = ParseRealmFile();

        foreach (var client in document.RootElement.GetProperty("clients").EnumerateArray())
        {
            var clientId = client.GetProperty("clientId").GetString();
            var hasSecret = client.TryGetProperty("secret", out _);

            if (clientId == "decisya-bff")
            {
                hasSecret.Should().BeTrue("decisya-bff is confidential and must carry a secret");
            }
            else
            {
                hasSecret.Should().BeFalse($"only decisya-bff should carry a client secret, not '{clientId}'");
            }
        }
    }

    [Fact]
    public void Every_seeded_users_credential_value_is_exactly_the_dev_password_placeholder()
    {
        using var document = ParseRealmFile();

        foreach (var user in document.RootElement.GetProperty("users").EnumerateArray())
        {
            foreach (var credential in user.GetProperty("credentials").EnumerateArray())
            {
                credential.GetProperty("value").GetString().Should().Be(RealmSecretRules.DevUserPasswordPlaceholderLiteral);
                credential.GetProperty("temporary").GetBoolean().Should().BeFalse();
            }
        }
    }

    [Fact]
    public void No_key_material_or_hashed_credential_field_appears_anywhere()
    {
        var content = ReadRealmFile();

        foreach (var forbidden in ForbiddenKeyMaterialTokens)
        {
            content.Should().NotContain(forbidden);
        }
    }

    [Fact]
    public void Every_seeded_user_has_a_dev_username_and_a_dot_test_email()
    {
        using var document = ParseRealmFile();

        foreach (var user in document.RootElement.GetProperty("users").EnumerateArray())
        {
            user.GetProperty("username").GetString().Should().StartWith("dev-");
            user.GetProperty("email").GetString().Should().EndWith(".test");
        }
    }

    [Fact]
    public void Components_hold_only_the_declarative_user_profile_provider()
    {
        using var document = ParseRealmFile();
        var components = document.RootElement.GetProperty("components");

        var propertyNames = components.EnumerateObject().Select(p => p.Name);
        propertyNames.Should().BeEquivalentTo(["org.keycloak.userprofile.UserProfileProvider"]);
    }

    [Fact]
    public void No_identity_provider_and_no_group_is_seeded()
    {
        using var document = ParseRealmFile();

        AssertAbsentOrEmpty(document.RootElement, "identityProviders");
        AssertAbsentOrEmpty(document.RootElement, "groups");
    }

    [Fact]
    public void No_seeded_user_carries_a_client_role_and_every_realm_role_is_one_of_the_two_platform_roles()
    {
        using var document = ParseRealmFile();

        foreach (var user in document.RootElement.GetProperty("users").EnumerateArray())
        {
            var username = user.GetProperty("username").GetString();
            user.TryGetProperty("clientRoles", out _).Should().BeFalse($"{username} must carry no clientRoles");

            if (user.TryGetProperty("realmRoles", out var realmRoles))
            {
                foreach (var role in realmRoles.EnumerateArray())
                {
                    AllowedRealmRoles.Should().Contain(role.GetString());
                }
            }
        }
    }

    [Fact]
    public void The_platform_admin_user_carries_no_tenant_id_attribute()
    {
        using var document = ParseRealmFile();

        var admin = document.RootElement.GetProperty("users").EnumerateArray()
            .Single(u => u.GetProperty("realmRoles").EnumerateArray().Any(r => r.GetString() == "platform-admin"));

        if (admin.TryGetProperty("attributes", out var attributes))
        {
            attributes.TryGetProperty("tenant_id", out _).Should().BeFalse(
                "platform-admin must carry no tenant_id attribute (the key must be absent, not empty)");
        }
    }

    [Fact]
    public void The_two_seeded_tenant_users_have_distinct_well_formed_tenant_ids()
    {
        using var document = ParseRealmFile();

        var tenantIds = document.RootElement.GetProperty("users").EnumerateArray()
            .Where(u => u.GetProperty("realmRoles").EnumerateArray().Any(r => r.GetString() == "tenant-user"))
            .Select(u => u.GetProperty("attributes").GetProperty("tenant_id").EnumerateArray().Single().GetString())
            .ToList();

        tenantIds.Should().HaveCountGreaterThanOrEqualTo(2);
        tenantIds.Should().OnlyHaveUniqueItems();

        foreach (var tenantId in tenantIds)
        {
            Guid.TryParse(tenantId, out var parsed).Should().BeTrue($"'{tenantId}' should be a well-formed GUID");
            parsed.Should().NotBe(Guid.Empty);
        }
    }

    /// <summary>
    /// #25 G2 D1 (G3 T-01): the flat <c>roles</c> claim in the access token, from the realm-role
    /// mapper, with exactly these flags. The ID-token mapper (#18) is unchanged.
    /// </summary>
    [Fact]
    public void The_decisya_bff_client_has_the_flat_roles_access_token_mapper_with_exact_flags()
    {
        using var document = ParseRealmFile();
        var mappers = FindClient(document, "decisya-bff").GetProperty("protocolMappers").EnumerateArray().ToList();

        var accessMapper = mappers.Single(m => m.GetProperty("name").GetString() == "realm-roles-access-token");
        accessMapper.GetProperty("protocol").GetString().Should().Be("openid-connect");
        accessMapper.GetProperty("protocolMapper").GetString().Should().Be("oidc-usermodel-realm-role-mapper");
        accessMapper.GetProperty("consentRequired").GetBoolean().Should().BeFalse();

        ConfigOf(accessMapper).Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["claim.name"] = "roles",
            ["multivalued"] = "true",
            ["jsonType.label"] = "String",
            ["id.token.claim"] = "false",
            ["access.token.claim"] = "true",
            ["userinfo.token.claim"] = "false",
            ["introspection.token.claim"] = "true",
        });
    }

    [Fact]
    public void The_realm_roles_id_token_mapper_is_unchanged_and_stays_out_of_the_access_token()
    {
        using var document = ParseRealmFile();
        var idMapper = FindClient(document, "decisya-bff").GetProperty("protocolMappers").EnumerateArray()
            .Single(m => m.GetProperty("name").GetString() == "realm-roles-id-token");

        idMapper.GetProperty("protocolMapper").GetString().Should().Be("oidc-usermodel-realm-role-mapper");
        ConfigOf(idMapper).Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["claim.name"] = "roles",
            ["multivalued"] = "true",
            ["id.token.claim"] = "true",
            ["access.token.claim"] = "false",
            ["userinfo.token.claim"] = "false",
        });
    }

    /// <summary>
    /// G3 T-01: no attribute (or any other) mapper writes the <c>roles</c> claim, so a user
    /// attribute can never reach it. Only the two realm-role mappers of <c>decisya-bff</c> do.
    /// </summary>
    [Fact]
    public void Only_the_two_realm_role_mappers_write_the_roles_claim()
    {
        using var document = ParseRealmFile();

        var writers = new List<string>();
        foreach (var client in document.RootElement.GetProperty("clients").EnumerateArray())
        {
            if (!client.TryGetProperty("protocolMappers", out var mappers))
            {
                continue;
            }

            foreach (var mapper in mappers.EnumerateArray())
            {
                if (mapper.GetProperty("config").TryGetProperty("claim.name", out var claim) && claim.GetString() == "roles")
                {
                    writers.Add($"{client.GetProperty("clientId").GetString()}/{mapper.GetProperty("name").GetString()}/{mapper.GetProperty("protocolMapper").GetString()}");
                }
            }
        }

        writers.Should().BeEquivalentTo(
        [
            "decisya-bff/realm-roles-id-token/oidc-usermodel-realm-role-mapper",
            "decisya-bff/realm-roles-access-token/oidc-usermodel-realm-role-mapper",
        ]);
    }

    private static Dictionary<string, string?> ConfigOf(JsonElement mapper) =>
        mapper.GetProperty("config").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());

    // G4-17-12 (T-02) moved to RealmGuardTests.No_file_in_the_working_tree_offends_the_scoped_guard
    // (issue #77, G4-77-03): the guard's rules are now a pure function (RealmGuard.Offends),
    // scoped by realm-guard-cases.json, instead of the inline, unanchored prefix check that
    // used to live here (T77-11). See docs/security/threat-models/realm-guard-scope.md.

    private static JsonElement FindClient(JsonDocument document, string clientId) =>
        document.RootElement.GetProperty("clients").EnumerateArray()
            .Single(c => c.GetProperty("clientId").GetString() == clientId);

    private static void AssertAbsentOrEmpty(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var value))
        {
            value.GetArrayLength().Should().Be(0, $"{propertyName} must be absent or empty");
        }
    }
}
