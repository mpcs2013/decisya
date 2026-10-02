using System.Net.Http.Headers;
using System.Text.Json;

namespace Decisya.Identity.Tests;

/// <summary>
/// The realm-configuration test ADR-0002's "Enforced by" line and G1 Story 7 ask for:
/// asserts every setting Stories 2-4 describe against a real, throwaway Keycloak instance
/// importing the committed <c>deploy/keycloak/decisya-realm.json</c> (G2, G4-17-05, 08, 10,
/// 11). Needs Docker; excluded from ci.yml's unit step by the trait below.
/// </summary>
[Trait("Category", "Integration")]
public class RealmConfigurationTests
{
    private readonly KeycloakRealmFixture _fixture;

    public RealmConfigurationTests(KeycloakRealmFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task The_realm_settings_match_the_platforms_security_invariants()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await CreateAdminClientAsync(cancellationToken);

        var realm = await GetJsonAsync(client, "/admin/realms/decisya", cancellationToken);

        realm.GetProperty("accessTokenLifespan").GetInt32().Should().BeLessThanOrEqualTo(300);
        realm.GetProperty("accessCodeLifespan").GetInt32().Should().BeLessThanOrEqualTo(60);
        realm.GetProperty("defaultSignatureAlgorithm").GetString().Should().BeOneOf("RS256", "ES256");
        realm.GetProperty("bruteForceProtected").GetBoolean().Should().BeTrue();
        realm.GetProperty("failureFactor").GetInt32().Should().BeGreaterThan(0);

        var policy = realm.GetProperty("passwordPolicy").GetString();
        policy.Should().Contain("length(");
        policy.Should().Contain("notUsername");

        realm.GetProperty("otpPolicyType").GetString().Should().Be("totp");
        realm.GetProperty("registrationAllowed").GetBoolean().Should().BeFalse();
        realm.GetProperty("sslRequired").GetString().Should().NotBe("none");
    }

    [Fact]
    public async Task Every_seeded_user_has_exactly_one_password_credential_after_import()
    {
        // G4 diagnostic (issue #17 live finding): confirms whether the users[].credentials
        // array actually produced a stored password credential at all, independent of
        // whether that credential's value matches anything — narrows down a placeholder
        // substitution failure to either "wrong value stored" or "no credential stored".
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await CreateAdminClientAsync(cancellationToken);

        foreach (var username in new[] { "dev-alice", "dev-bob", "dev-admin" })
        {
            var users = await GetJsonAsync(client, $"/admin/realms/decisya/users?username={username}&exact=true", cancellationToken);
            var userId = users.EnumerateArray().Single().GetProperty("id").GetString();

            var credentials = await GetJsonAsync(client, $"/admin/realms/decisya/users/{userId}/credentials", cancellationToken);
            var passwordCredentials = credentials.EnumerateArray()
                .Where(c => c.GetProperty("type").GetString() == "password")
                .ToList();

            passwordCredentials.Should().HaveCount(1, $"{username} should have exactly one password credential after import");
        }
    }

    [Fact]
    public async Task The_CONFIGURE_TOTP_required_action_is_enabled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await CreateAdminClientAsync(cancellationToken);

        var requiredAction = await GetJsonAsync(
            client, "/admin/realms/decisya/authentication/required-actions/CONFIGURE_TOTP", cancellationToken);

        requiredAction.GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task The_decisya_bff_client_matches_every_named_setting()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await CreateAdminClientAsync(cancellationToken);

        var bff = await GetSingleClientAsync(client, "decisya-bff", cancellationToken);

        bff.GetProperty("publicClient").GetBoolean().Should().BeFalse();
        bff.GetProperty("clientAuthenticatorType").GetString().Should().Be("client-secret");
        bff.GetProperty("standardFlowEnabled").GetBoolean().Should().BeTrue();
        bff.GetProperty("implicitFlowEnabled").GetBoolean().Should().BeFalse();
        bff.GetProperty("directAccessGrantsEnabled").GetBoolean().Should().BeFalse();
        bff.GetProperty("serviceAccountsEnabled").GetBoolean().Should().BeFalse();
        bff.GetProperty("fullScopeAllowed").GetBoolean().Should().BeFalse();

        var attributes = bff.GetProperty("attributes");
        attributes.GetProperty("pkce.code.challenge.method").GetString().Should().Be("S256");

        if (attributes.TryGetProperty("access.token.lifespan", out var clientLifespan))
        {
            int.Parse(clientLifespan.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
                .Should().BeLessThanOrEqualTo(300);
        }

        foreach (var algAttribute in new[] { "access.token.signed.response.alg", "id.token.signed.response.alg" })
        {
            attributes.GetProperty(algAttribute).GetString().Should().BeOneOf("RS256", "ES256");
        }

        string[] forbiddenAlgorithms = ["HS256", "HS384", "HS512", "none"];
        foreach (var attribute in attributes.EnumerateObject())
        {
            var value = attribute.Value.GetString();
            var isForbidden = value is not null
                && forbiddenAlgorithms.Any(forbidden => string.Equals(value, forbidden, StringComparison.OrdinalIgnoreCase));

            isForbidden.Should().BeFalse($"client attribute '{attribute.Name}' must not be an HMAC/none algorithm, was '{value}'");
        }

        attributes.GetProperty("request.object.signature.alg").GetString().Should().Be("RS256");

        var redirectUris = bff.GetProperty("redirectUris").EnumerateArray().Select(e => e.GetString()).ToList();
        redirectUris.Should().BeEquivalentTo(["https://localhost:7200/signin-oidc"]);
        redirectUris.Should().NotContain(uri => uri!.Contains('*', StringComparison.Ordinal));
        redirectUris.Should().NotContain(uri => uri!.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

        attributes.GetProperty("post.logout.redirect.uris").GetString().Should().NotContain("*").And.NotStartWith("http://");

        bff.GetProperty("webOrigins").GetArrayLength().Should().Be(0);

        var defaultScopes = bff.GetProperty("defaultClientScopes").EnumerateArray().Select(e => e.GetString()).ToList();
        defaultScopes.Should().BeEquivalentTo(["basic", "profile", "email", "roles", "acr", "web-origins"]);
        bff.GetProperty("optionalClientScopes").GetArrayLength().Should().Be(0);

        foreach (var deviceGrantAttribute in new[] { "oauth2.device.authorization.grant.enabled", "oidc.ciba.grant.enabled" })
        {
            attributes.GetProperty(deviceGrantAttribute).GetString().Should().Be("false");
        }
    }

    [Fact]
    public async Task The_tenant_id_and_audience_protocol_mappers_are_present_and_enabled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await CreateAdminClientAsync(cancellationToken);

        var bff = await GetSingleClientAsync(client, "decisya-bff", cancellationToken);
        var clientUuid = bff.GetProperty("id").GetString();

        var mappers = await GetJsonAsync(
            client, $"/admin/realms/decisya/clients/{clientUuid}/protocol-mappers/models", cancellationToken);

        var tenantMapper = mappers.EnumerateArray().Single(m => m.GetProperty("name").GetString() == "tenant_id");
        tenantMapper.GetProperty("protocolMapper").GetString().Should().Be("oidc-usermodel-attribute-mapper");
        var tenantConfig = tenantMapper.GetProperty("config");
        tenantConfig.GetProperty("user.attribute").GetString().Should().Be("tenant_id");
        tenantConfig.GetProperty("claim.name").GetString().Should().Be("tenant_id");
        tenantConfig.GetProperty("id.token.claim").GetString().Should().Be("true");
        tenantConfig.GetProperty("access.token.claim").GetString().Should().Be("true");

        var audienceMapper = mappers.EnumerateArray().Single(m => m.GetProperty("name").GetString() == "audience-decisya-api");
        audienceMapper.GetProperty("protocolMapper").GetString().Should().Be("oidc-audience-mapper");
        var audienceConfig = audienceMapper.GetProperty("config");
        audienceConfig.GetProperty("included.custom.audience").GetString().Should().Be("decisya-api");
        audienceConfig.GetProperty("access.token.claim").GetString().Should().Be("true");
    }

    /// <summary>
    /// #18 G2/G3: the client-level realm-roles-into-ID-token mapper the BFF's ID-token-only
    /// identity model depends on (D5) — the built-in "roles" scope puts realm roles only in
    /// the access token, which the BFF never parses.
    /// </summary>
    [Fact]
    public async Task The_realm_roles_id_token_mapper_puts_roles_in_the_ID_token_only()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await CreateAdminClientAsync(cancellationToken);

        var bff = await GetSingleClientAsync(client, "decisya-bff", cancellationToken);
        var clientUuid = bff.GetProperty("id").GetString();

        var mappers = await GetJsonAsync(
            client, $"/admin/realms/decisya/clients/{clientUuid}/protocol-mappers/models", cancellationToken);

        var rolesMapper = mappers.EnumerateArray().Single(m => m.GetProperty("name").GetString() == "realm-roles-id-token");
        rolesMapper.GetProperty("protocolMapper").GetString().Should().Be("oidc-usermodel-realm-role-mapper");

        var rolesConfig = rolesMapper.GetProperty("config");
        rolesConfig.GetProperty("claim.name").GetString().Should().Be("roles");
        rolesConfig.GetProperty("multivalued").GetString().Should().Be("true");
        rolesConfig.GetProperty("id.token.claim").GetString().Should().Be("true");
        rolesConfig.GetProperty("access.token.claim").GetString().Should().Be("false");
        rolesConfig.GetProperty("userinfo.token.claim").GetString().Should().Be("false");
    }

    /// <summary>
    /// #25 G2 D1: the client-level mapper that puts a flat <c>roles</c> array into the access
    /// token only. The API reads this claim and nothing else for the platform-admin role.
    /// </summary>
    [Fact]
    public async Task The_realm_roles_access_token_mapper_puts_a_flat_roles_claim_in_the_access_token_only()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await CreateAdminClientAsync(cancellationToken);

        var bff = await GetSingleClientAsync(client, "decisya-bff", cancellationToken);
        var clientUuid = bff.GetProperty("id").GetString();

        var mappers = await GetJsonAsync(
            client, $"/admin/realms/decisya/clients/{clientUuid}/protocol-mappers/models", cancellationToken);

        var rolesMapper = mappers.EnumerateArray().Single(m => m.GetProperty("name").GetString() == "realm-roles-access-token");
        rolesMapper.GetProperty("protocolMapper").GetString().Should().Be("oidc-usermodel-realm-role-mapper");

        var rolesConfig = rolesMapper.GetProperty("config");
        rolesConfig.GetProperty("claim.name").GetString().Should().Be("roles");
        rolesConfig.GetProperty("multivalued").GetString().Should().Be("true");
        rolesConfig.GetProperty("jsonType.label").GetString().Should().Be("String");
        rolesConfig.GetProperty("id.token.claim").GetString().Should().Be("false");
        rolesConfig.GetProperty("access.token.claim").GetString().Should().Be("true");
        rolesConfig.GetProperty("userinfo.token.claim").GetString().Should().Be("false");
        rolesConfig.GetProperty("introspection.token.claim").GetString().Should().Be("true");
    }

    /// <summary>
    /// #18 G2 (identity-dev): the back-channel-logout URL the realm calls on an
    /// administrator-initiated Keycloak logout. #18's own automated tests do not depend on
    /// this path reaching a live BFF (G2's stop-and-record rule; S-6); this only pins the
    /// realm's own configuration.
    /// </summary>
    [Fact]
    public async Task The_decisya_bff_client_has_a_backchannel_logout_url_and_no_frontchannel_logout()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await CreateAdminClientAsync(cancellationToken);

        var bff = await GetSingleClientAsync(client, "decisya-bff", cancellationToken);

        // S-7: the front-channel sign-out endpoint stays disabled; the BFF relies on
        // back-channel logout and RP-initiated logout only.
        bff.GetProperty("frontchannelLogout").GetBoolean().Should().BeFalse();

        var attributes = bff.GetProperty("attributes");
        attributes.GetProperty("backchannel.logout.session.required").GetString().Should().Be("true");

        var backchannelLogoutUrl = attributes.GetProperty("backchannel.logout.url").GetString();
        backchannelLogoutUrl.Should().Be("https://host.docker.internal:7200/bff/backchannel-logout");
        backchannelLogoutUrl.Should().StartWith("https://").And.NotContain("*");
    }

    [Fact]
    public async Task The_stored_client_secret_equals_the_environment_value_the_placeholder_resolved_to()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await CreateAdminClientAsync(cancellationToken);

        var bff = await GetSingleClientAsync(client, "decisya-bff", cancellationToken);
        var clientUuid = bff.GetProperty("id").GetString();

        var secretResponse = await GetJsonAsync(
            client, $"/admin/realms/decisya/clients/{clientUuid}/client-secret", cancellationToken);
        var storedSecret = secretResponse.GetProperty("value").GetString() ?? string.Empty;

        // Boolean/fixed-time comparison only (#15 L-1): the secret itself never reaches an
        // assertion message.
        var matches = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(storedSecret),
            System.Text.Encoding.UTF8.GetBytes(_fixture.ClientSecret));

        matches.Should().BeTrue("the imported client secret should equal the environment value the placeholder resolved to");
    }

    [Fact]
    public async Task The_tenant_id_user_profile_attribute_is_admin_only_and_unmanaged_attributes_stay_disabled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await CreateAdminClientAsync(cancellationToken);

        var profile = await GetJsonAsync(client, "/admin/realms/decisya/users/profile", cancellationToken);
        var attributes = profile.GetProperty("attributes");
        var tenantIdAttribute = attributes.EnumerateArray().Single(a => a.GetProperty("name").GetString() == "tenant_id");

        var permissions = tenantIdAttribute.GetProperty("permissions");
        permissions.GetProperty("edit").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(["admin"]);
        permissions.GetProperty("view").EnumerateArray().Select(e => e.GetString()).Should().NotContain("user");

        profile.TryGetProperty("unmanagedAttributePolicy", out _).Should().BeFalse();
    }

    [Fact]
    public async Task At_least_two_enabled_tenant_users_have_distinct_tenant_ids_and_one_platform_admin_has_none()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await CreateAdminClientAsync(cancellationToken);

        var tenantUsers = await GetJsonAsync(client, "/admin/realms/decisya/roles/tenant-user/users", cancellationToken);
        var tenantIds = new List<string>();

        foreach (var user in tenantUsers.EnumerateArray())
        {
            user.GetProperty("enabled").GetBoolean().Should().BeTrue();
            user.GetProperty("email").GetString().Should().EndWith(".test");

            var attributes = user.GetProperty("attributes");
            var tenantId = attributes.GetProperty("tenant_id").EnumerateArray().Single().GetString()!;
            Guid.TryParse(tenantId, out var parsed).Should().BeTrue();
            parsed.Should().NotBe(Guid.Empty);
            tenantIds.Add(tenantId);
        }

        tenantIds.Should().HaveCountGreaterThanOrEqualTo(2);
        tenantIds.Should().OnlyHaveUniqueItems();

        var admins = await GetJsonAsync(client, "/admin/realms/decisya/roles/platform-admin/users", cancellationToken);
        var enabledAdmins = admins.EnumerateArray().Where(u => u.GetProperty("enabled").GetBoolean()).ToList();

        enabledAdmins.Should().HaveCount(1);
        enabledAdmins[0].TryGetProperty("attributes", out _).Should().BeFalse();
    }

    [Fact]
    public async Task No_seeded_user_is_composite_or_carries_any_realm_management_client_role()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await CreateAdminClientAsync(cancellationToken);

        foreach (var roleName in new[] { "tenant-user", "platform-admin" })
        {
            var role = await GetJsonAsync(client, $"/admin/realms/decisya/roles/{roleName}", cancellationToken);
            role.GetProperty("composite").GetBoolean().Should().BeFalse();
        }

        var allUsers = await GetJsonAsync(client, "/admin/realms/decisya/users?briefRepresentation=false", cancellationToken);

        foreach (var user in allUsers.EnumerateArray())
        {
            var userId = user.GetProperty("id").GetString();
            var roleMappings = await GetJsonAsync(
                client, $"/admin/realms/decisya/users/{userId}/role-mappings", cancellationToken);

            if (roleMappings.TryGetProperty("clientMappings", out var clientMappings))
            {
                clientMappings.TryGetProperty("realm-management", out _).Should().BeFalse(
                    $"{user.GetProperty("username").GetString()} must carry no realm-management client role");
            }
        }
    }

    [Fact]
    public async Task No_identity_provider_is_configured()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = await CreateAdminClientAsync(cancellationToken);

        var identityProviders = await GetJsonAsync(client, "/admin/realms/decisya/identity-provider/instances", cancellationToken);

        identityProviders.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void The_tenant_id_pattern_accepts_the_seed_values_and_rejects_the_all_zero_GUID()
    {
        var pattern = ExtractTenantIdPatternFromRealmFile();
        var regex = new System.Text.RegularExpressions.Regex(pattern);

        regex.IsMatch("7c9e6679-7425-40de-944b-e07fc1f90ae7").Should().BeTrue();
        regex.IsMatch("2f1a8d5e-3b4c-4e6f-9a7b-1c2d3e4f5a6b").Should().BeTrue();
        regex.IsMatch("00000000-0000-0000-0000-000000000000").Should().BeFalse();
        regex.IsMatch("7C9E6679-7425-40DE-944B-E07FC1F90AE7").Should().BeFalse();
        regex.IsMatch("{7c9e6679-7425-40de-944b-e07fc1f90ae7}").Should().BeFalse();
        regex.IsMatch(string.Empty).Should().BeFalse();
    }

    private static string ExtractTenantIdPatternFromRealmFile()
    {
        var content = File.ReadAllText(RepoPaths.Find(Path.Combine("deploy", "keycloak", "decisya-realm.json")));
        using var document = JsonDocument.Parse(content);

        var upConfigString = document.RootElement
            .GetProperty("components")
            .GetProperty("org.keycloak.userprofile.UserProfileProvider")[0]
            .GetProperty("config")
            .GetProperty("kc.user.profile.config")[0]
            .GetString()!;

        using var upDocument = JsonDocument.Parse(upConfigString);
        var tenantIdAttribute = upDocument.RootElement.GetProperty("attributes")
            .EnumerateArray().Single(a => a.GetProperty("name").GetString() == "tenant_id");

        return tenantIdAttribute.GetProperty("validations").GetProperty("pattern").GetProperty("pattern").GetString()!;
    }

    private async Task<HttpClient> CreateAdminClientAsync(CancellationToken cancellationToken)
    {
        var token = await _fixture.GetAdminAccessTokenAsync(cancellationToken);
        var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> GetSingleClientAsync(HttpClient client, string clientId, CancellationToken cancellationToken)
    {
        var results = await GetJsonAsync(client, $"/admin/realms/decisya/clients?clientId={clientId}", cancellationToken);
        return results.EnumerateArray().Single();
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.Clone();
    }
}
