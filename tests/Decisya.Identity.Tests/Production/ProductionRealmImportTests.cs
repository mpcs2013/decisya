using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

namespace Decisya.Identity.Tests;

/// <summary>
/// Issue #121 (G2 D1/D3/D4/D5): the production realm imported by the real wrapper into the pinned
/// Keycloak, and read back through the admin REST API. Needs Docker; excluded from the unit lane
/// by the trait.
/// </summary>
[Trait("Category", "Integration")]
[Collection(ProductionKeycloakDefinition.Name)]
public sealed class ProductionRealmImportTests
{
    private const string ExpectedPasswordPolicy =
        "length(12) and maxLength(128) and notUsername and notContainsUsername and notEmail and passwordHistory(3) and passwordBlacklist(common-passwords.txt)";

    private readonly ProductionKeycloak _stack;

    public ProductionRealmImportTests(ProductionKeycloak stack)
    {
        _stack = stack;
    }

    [Fact]
    public async Task The_production_realm_imports_with_no_seeded_users()
    {
        var ct = TestContext.Current.CancellationToken;
        await _stack.EnsureStartedAsync(ct);

        _stack.UsersRightAfterImport.Should().Be(0);
    }

    [Fact]
    public async Task The_realm_settings_match_the_production_invariants()
    {
        var ct = TestContext.Current.CancellationToken;
        await _stack.EnsureStartedAsync(ct);
        using var admin = await _stack.AdminClientAsync(ct);

        var realm = await ProductionKeycloak.GetJsonAsync(admin, $"/admin/realms/{ProductionKeycloak.Realm}", ct);

        realm.GetProperty("sslRequired").GetString().Should().Be("all");
        realm.GetProperty("registrationAllowed").GetBoolean().Should().BeFalse();
        realm.GetProperty("resetPasswordAllowed").GetBoolean().Should().BeFalse();
        realm.GetProperty("rememberMe").GetBoolean().Should().BeFalse();
        realm.GetProperty("accessTokenLifespan").GetInt32().Should().BeLessThanOrEqualTo(300);
        realm.GetProperty("accessCodeLifespan").GetInt32().Should().BeLessThanOrEqualTo(60);
        realm.GetProperty("defaultSignatureAlgorithm").GetString().Should().BeOneOf("RS256", "ES256");
        realm.GetProperty("bruteForceProtected").GetBoolean().Should().BeTrue();
        realm.GetProperty("passwordPolicy").GetString().Should().Be(ExpectedPasswordPolicy);
        realm.GetProperty("otpPolicyType").GetString().Should().Be("totp");
        realm.GetProperty("otpPolicyDigits").GetInt32().Should().Be(6);
        realm.GetProperty("otpPolicyPeriod").GetInt32().Should().Be(30);
        realm.GetProperty("browserFlow").GetString().Should().Be("decisya-browser");
        realm.GetProperty("attributes").GetProperty("acr.loa.map").GetString().Should().Be("{\"1\":1,\"2\":2}");
    }

    /// <summary>G4-121-04 (e): the event settings are pinned, in the running Keycloak.</summary>
    [Fact]
    public async Task The_keycloak_event_settings_are_pinned()
    {
        var ct = TestContext.Current.CancellationToken;
        await _stack.EnsureStartedAsync(ct);
        using var admin = await _stack.AdminClientAsync(ct);

        var events = await ProductionKeycloak.GetJsonAsync(admin, $"/admin/realms/{ProductionKeycloak.Realm}/events/config", ct);
        var realm = await ProductionKeycloak.GetJsonAsync(admin, $"/admin/realms/{ProductionKeycloak.Realm}", ct);

        events.GetProperty("eventsEnabled").GetBoolean().Should().BeTrue();
        events.GetProperty("eventsListeners").GetArrayLength().Should().Be(0, "Q5: no jboss-logging listener, the store only");
        events.GetProperty("adminEventsEnabled").GetBoolean().Should().BeTrue();
        events.GetProperty("adminEventsDetailsEnabled").GetBoolean().Should().BeFalse();
        events.GetProperty("eventsExpiration").GetInt64().Should().Be(7776000);
        realm.GetProperty("attributes").GetProperty("adminEventsExpiration").GetString().Should().Be("7776000");

        events.GetProperty("enabledEventTypes").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(
        [
            "LOGIN", "LOGIN_ERROR", "LOGOUT", "LOGOUT_ERROR", "CODE_TO_TOKEN_ERROR", "REFRESH_TOKEN_ERROR",
            "UPDATE_PASSWORD", "UPDATE_PASSWORD_ERROR", "UPDATE_TOTP", "REMOVE_TOTP",
            "USER_DISABLED_BY_TEMPORARY_LOCKOUT", "USER_DISABLED_BY_PERMANENT_LOCKOUT",
        ]);
    }

    [Fact]
    public async Task The_step_up_browser_flow_has_the_level_2_admin_switch_on_and_the_tenant_switch_off()
    {
        var ct = TestContext.Current.CancellationToken;
        await _stack.EnsureStartedAsync(ct);
        using var admin = await _stack.AdminClientAsync(ct);

        var flows = await ProductionKeycloak.GetJsonAsync(admin, $"/admin/realms/{ProductionKeycloak.Realm}/authentication/flows", ct);
        var aliases = flows.EnumerateArray().Select(f => f.GetProperty("alias").GetString()).ToList();
        aliases.Should().Contain("decisya-browser");
        aliases.Should().Contain("browser", "Keycloak keeps its built-in flows next to the imported ones");

        var executions = await ProductionKeycloak.GetJsonAsync(
            admin, $"/admin/realms/{ProductionKeycloak.Realm}/authentication/flows/decisya-forms/executions", ct);
        var byName = executions.EnumerateArray()
            .Where(e => e.TryGetProperty("authenticationFlow", out var isFlow) && isFlow.GetBoolean())
            .ToDictionary(e => e.GetProperty("displayName").GetString()!, e => e.GetProperty("requirement").GetString()!);

        byName["level-1"].Should().Be("CONDITIONAL");
        byName["level-2-admin"].Should().Be("CONDITIONAL");
        byName["level-2-tenant"].Should().Be("DISABLED", "C-02: the tenant-user level-2 switch is present and off");

        // The admin sub-flow: level 2 condition, role condition for platform-admin, then the OTP form.
        var adminExecutions = await ProductionKeycloak.GetJsonAsync(
            admin, $"/admin/realms/{ProductionKeycloak.Realm}/authentication/flows/level-2-admin/executions", ct);
        adminExecutions.EnumerateArray().Select(e => e.GetProperty("providerId").GetString()).Should().Equal(
            "conditional-level-of-authentication", "conditional-user-role", "auth-otp-form");
        adminExecutions.EnumerateArray().Select(e => e.GetProperty("requirement").GetString()).Should().OnlyContain(r => r == "REQUIRED");

        var configs = new List<Dictionary<string, string>>();
        foreach (var execution in adminExecutions.EnumerateArray().Where(e => e.TryGetProperty("authenticationConfig", out _)))
        {
            var id = execution.GetProperty("authenticationConfig").GetString();
            var config = await ProductionKeycloak.GetJsonAsync(admin, $"/admin/realms/{ProductionKeycloak.Realm}/authentication/config/{id}", ct);
            configs.Add(config.GetProperty("config").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!));
        }

        configs.Should().HaveCount(2);
        configs[0]["loa-condition-level"].Should().Be("2");
        configs[0]["loa-max-age"].Should().Be("1800");
        configs[1]["condUserRole"].Should().Be("platform-admin");
        configs[1]["negate"].Should().Be("false");
    }

    [Fact]
    public async Task CONFIGURE_TOTP_and_UPDATE_PASSWORD_are_enabled_required_actions()
    {
        var ct = TestContext.Current.CancellationToken;
        await _stack.EnsureStartedAsync(ct);
        using var admin = await _stack.AdminClientAsync(ct);

        var all = await ProductionKeycloak.GetJsonAsync(
            admin, $"/admin/realms/{ProductionKeycloak.Realm}/authentication/required-actions", ct);
        var summary = string.Join(
            "; ",
            all.EnumerateArray().Select(a =>
                $"{a.GetProperty("alias").GetString()}(enabled={a.GetProperty("enabled")},default={a.GetProperty("defaultAction")},priority={a.GetProperty("priority")},provider={a.GetProperty("providerId").GetString()})"));
        ProductionKeycloak.Record("required actions after import: " + summary);

        foreach (var alias in new[] { "CONFIGURE_TOTP", "UPDATE_PASSWORD" })
        {
            var action = all.EnumerateArray().SingleOrDefault(a => a.GetProperty("alias").GetString() == alias);
            action.ValueKind.Should().NotBe(System.Text.Json.JsonValueKind.Undefined, $"{alias} must exist; the realm has: {summary}");
            action.GetProperty("enabled").GetBoolean().Should().BeTrue(alias);
            action.GetProperty("defaultAction").GetBoolean().Should().BeFalse($"{alias} must not be forced on every user");
        }
    }

    [Fact]
    public async Task The_synthetic_attribute_is_admin_only_and_only_accepts_true()
    {
        var ct = TestContext.Current.CancellationToken;
        await _stack.EnsureStartedAsync(ct);
        using var admin = await _stack.AdminClientAsync(ct);

        var profile = await ProductionKeycloak.GetJsonAsync(admin, $"/admin/realms/{ProductionKeycloak.Realm}/users/profile", ct);
        var attributes = profile.GetProperty("attributes").EnumerateArray().ToList();
        var synthetic = attributes.Single(a => a.GetProperty("name").GetString() == "synthetic");

        synthetic.GetProperty("permissions").GetProperty("edit").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(["admin"]);
        synthetic.GetProperty("permissions").GetProperty("view").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(["admin"]);
        synthetic.GetProperty("validations").GetProperty("pattern").GetProperty("pattern").GetString().Should().Be("^true$");
        profile.TryGetProperty("unmanagedAttributePolicy", out _).Should().BeFalse();

        var tenantId = attributes.Single(a => a.GetProperty("name").GetString() == "tenant_id");
        tenantId.GetProperty("permissions").GetProperty("edit").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(["admin"]);
    }

    [Fact]
    public async Task The_decisya_bff_client_has_the_resolved_origin_and_the_wrapper_read_secret()
    {
        var ct = TestContext.Current.CancellationToken;
        await _stack.EnsureStartedAsync(ct);
        using var admin = await _stack.AdminClientAsync(ct);

        var clients = await ProductionKeycloak.GetJsonAsync(admin, $"/admin/realms/{ProductionKeycloak.Realm}/clients?clientId=decisya-bff", ct);
        var bff = clients.EnumerateArray().Single();

        bff.GetProperty("publicClient").GetBoolean().Should().BeFalse();
        bff.GetProperty("standardFlowEnabled").GetBoolean().Should().BeTrue();
        bff.GetProperty("implicitFlowEnabled").GetBoolean().Should().BeFalse();
        bff.GetProperty("directAccessGrantsEnabled").GetBoolean().Should().BeFalse();
        bff.GetProperty("serviceAccountsEnabled").GetBoolean().Should().BeFalse();
        bff.GetProperty("fullScopeAllowed").GetBoolean().Should().BeFalse();
        bff.GetProperty("frontchannelLogout").GetBoolean().Should().BeFalse();
        bff.GetProperty("webOrigins").GetArrayLength().Should().Be(0);
        bff.GetProperty("redirectUris").EnumerateArray().Select(e => e.GetString()).Should().Equal(ProductionKeycloak.AppOrigin + "/signin-oidc");
        bff.GetProperty("defaultClientScopes").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(
            ["basic", "profile", "email", "roles", "acr", "web-origins"]);

        var attributes = bff.GetProperty("attributes");
        attributes.GetProperty("pkce.code.challenge.method").GetString().Should().Be("S256");
        attributes.GetProperty("post.logout.redirect.uris").GetString().Should().Be(ProductionKeycloak.AppOrigin + "/signout-callback-oidc");
        attributes.GetProperty("backchannel.logout.url").GetString().Should().Be(ProductionKeycloak.AppOrigin + "/bff/backchannel-logout");
        attributes.GetProperty("backchannel.logout.session.required").GetString().Should().Be("true");

        // Nothing unresolved survived the import, anywhere in the client.
        bff.GetRawText().Should().NotContain("${");

        var uuid = bff.GetProperty("id").GetString();
        var secret = await ProductionKeycloak.GetJsonAsync(admin, $"/admin/realms/{ProductionKeycloak.Realm}/clients/{uuid}/client-secret", ct);
        var stored = Encoding.UTF8.GetBytes(secret.GetProperty("value").GetString() ?? string.Empty);
        CryptographicOperations.FixedTimeEquals(stored, Encoding.UTF8.GetBytes(_stack.ClientSecret)).Should().BeTrue(
            "the wrapper must have read the secret file and substituted it into the realm");
    }

    /// <summary>The roles and mappers the Api and the BFF depend on are the dev realm's (parity is also checked statically).</summary>
    [Fact]
    public async Task The_roles_and_the_four_protocol_mappers_are_present()
    {
        var ct = TestContext.Current.CancellationToken;
        await _stack.EnsureStartedAsync(ct);
        using var admin = await _stack.AdminClientAsync(ct);

        var clients = await ProductionKeycloak.GetJsonAsync(admin, $"/admin/realms/{ProductionKeycloak.Realm}/clients?clientId=decisya-bff", ct);
        var uuid = clients.EnumerateArray().Single().GetProperty("id").GetString();
        var mappers = await ProductionKeycloak.GetJsonAsync(
            admin, $"/admin/realms/{ProductionKeycloak.Realm}/clients/{uuid}/protocol-mappers/models", ct);

        mappers.EnumerateArray().Select(m => m.GetProperty("name").GetString()).Should().BeEquivalentTo(
            ["tenant_id", "audience-decisya-api", "realm-roles-id-token", "realm-roles-access-token"]);

        foreach (var role in new[] { "tenant-user", "platform-admin" })
        {
            var rep = await ProductionKeycloak.GetJsonAsync(admin, $"/admin/realms/{ProductionKeycloak.Realm}/roles/{role}", ct);
            rep.GetProperty("composite").GetBoolean().Should().BeFalse(role);
        }

        var identityProviders = await ProductionKeycloak.GetJsonAsync(
            admin, $"/admin/realms/{ProductionKeycloak.Realm}/identity-provider/instances", ct);
        identityProviders.GetArrayLength().Should().Be(0);
    }

    /// <summary>
    /// C-02, V6.2.4 and V6.2.11: Keycloak loads <c>common-passwords.txt</c> from
    /// <c>/opt/keycloak/data/password-blacklists/</c> (the default blacklists path) and refuses a
    /// listed password. A control password, not on the list, is accepted in the same flow.
    /// </summary>
    [Fact]
    public async Task The_password_list_loads_and_refuses_a_listed_password()
    {
        var ct = TestContext.Current.CancellationToken;
        await _stack.EnsureStartedAsync(ct);
        using var admin = await _stack.AdminClientAsync(ct);

        var listed = File.ReadLines(RepoPaths.Find(Path.Combine("deploy", "keycloak", "production", "common-passwords.txt")))
            .First(line => line.Length >= 12);

        var username = "t-" + ProductionKeycloak.Hex(12);
        using (var create = await admin.PostAsJsonAsync(
            $"/admin/realms/{ProductionKeycloak.Realm}/users",
            new
            {
                username,
                email = username + "@decisya.invalid",
                enabled = true,
                firstName = "Synthetic",
                lastName = "User",
                attributes = new Dictionary<string, string[]> { ["synthetic"] = ["true"], ["tenant_id"] = [ProductionKeycloak.DefaultTenantId] },
            },
            ct))
        {
            create.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        var users = await ProductionKeycloak.GetJsonAsync(admin, $"/admin/realms/{ProductionKeycloak.Realm}/users?username={username}&exact=true", ct);
        var userId = users.EnumerateArray().Single().GetProperty("id").GetString();
        var resetPath = $"/admin/realms/{ProductionKeycloak.Realm}/users/{userId}/reset-password";

        try
        {
            using var refused = await admin.PutAsJsonAsync(resetPath, new { type = "password", value = listed, temporary = false }, ct);
            var refusedBody = await refused.Content.ReadAsStringAsync(ct);
            ProductionKeycloak.Record($"blacklist: listed password -> {(int)refused.StatusCode} {ProductionKeycloak.Truncate(refusedBody, 200)}");
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            refusedBody.Should().Contain("lacklist", "the refusal must come from the blacklist policy, not another rule");

            using var control = await admin.PutAsJsonAsync(
                resetPath, new { type = "password", value = ProductionKeycloak.Hex(24), temporary = false }, ct);
            control.StatusCode.Should().Be(HttpStatusCode.NoContent, "an unlisted password of the right length must pass the whole policy");

            using var upper = await admin.PutAsJsonAsync(
                resetPath, new { type = "password", value = listed.ToUpperInvariant(), temporary = false }, ct);
            ProductionKeycloak.Record($"blacklist: upper-cased listed password -> {(int)upper.StatusCode}");

            using var containsName = await admin.PutAsJsonAsync(
                resetPath, new { type = "password", value = "Qx9-" + username + "-Zr7!", temporary = false }, ct);
            containsName.StatusCode.Should().Be(HttpStatusCode.BadRequest, "notContainsUsername is part of the production policy");
        }
        finally
        {
            await _stack.DeleteUserAsync(username, ct);
        }
    }

    /// <summary>
    /// G2 D5: without the list at its path Keycloak must not serve the realm (the policy cannot be
    /// parsed), so a stack that lost its mount fails closed instead of running without the check.
    /// </summary>
    [Fact]
    public async Task A_stack_without_the_password_list_does_not_serve_the_realm()
    {
        var ct = TestContext.Current.CancellationToken;
        var container = ProductionKeycloak.BuildKeycloak(
            network: null,
            clientSecret: ProductionKeycloak.Hex(32),
            dbPassword: ProductionKeycloak.Hex(24),
            bootstrapPassword: ProductionKeycloak.Hex(24),
            withPasswordList: false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(4));

            var started = true;
            try
            {
                await container.StartAsync(timeout.Token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || timeout.IsCancellationRequested)
            {
                started = false;
            }

            var (stdout, stderr) = await container.GetLogsAsync(ct: CancellationToken.None);
            var log = stdout + stderr;
            ProductionKeycloak.Record($"missing password list: container started={started}; log mentions the list={log.Contains("common-passwords", StringComparison.Ordinal)}");

            if (started)
            {
                using var http = new HttpClient { BaseAddress = new Uri($"http://{container.Hostname}:{container.GetMappedPublicPort(8080)}") };
                http.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
                using var response = await http.GetAsync($"/realms/{ProductionKeycloak.Realm}/.well-known/openid-configuration", ct);
                response.StatusCode.Should().Be(HttpStatusCode.NotFound, "the realm must not exist without its password list");
            }
            else
            {
                log.Should().Contain("common-passwords", "the failure should name the missing list");
            }
        }
        finally
        {
            await container.DisposeAsync();
        }
    }

    /// <summary>
    /// Schema discovery for <c>identity-check.sql</c>: the table columns it relies on, recorded so a
    /// Keycloak bump that renames one fails loudly here rather than silently in the NAS check.
    /// </summary>
    [Fact]
    public async Task The_tables_the_identity_check_reads_have_the_columns_it_uses()
    {
        var ct = TestContext.Current.CancellationToken;
        await _stack.EnsureStartedAsync(ct);

        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["realm"] = ["id", "name", "ssl_required", "events_enabled", "events_expiration", "admin_events_enabled", "admin_events_details_enabled", "browser_flow", "password_policy"],
            ["realm_attribute"] = ["realm_id", "name", "value"],
            ["realm_events_listeners"] = ["realm_id", "value"],
            ["authentication_flow"] = ["id", "alias", "realm_id"],
            ["authentication_execution"] = ["id", "flow_id", "requirement", "auth_flow_id"],
            ["user_entity"] = ["id", "realm_id", "service_account_client_link"],
            ["user_attribute"] = ["user_id", "name", "value"],
            ["credential"] = ["user_id", "type"],
            ["client"] = ["id", "client_id", "secret", "realm_id"],
            ["redirect_uris"] = ["client_id", "value"],
            ["client_attributes"] = ["client_id", "name", "value"],
        };

        foreach (var (table, columns) in expected)
        {
            var found = await _stack.QueryAsync(
                $"SELECT column_name FROM information_schema.columns WHERE table_name = '{table}' ORDER BY column_name", ct);
            var present = found.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            present.Should().Contain(columns, $"table {table}: found [{string.Join(",", present)}]");
        }

        var requirements = await _stack.QueryAsync(
            "SELECT f.alias || ':' || e.requirement FROM authentication_flow f JOIN authentication_execution e ON e.auth_flow_id = f.id "
            + "WHERE f.alias LIKE 'level-%' ORDER BY f.alias", ct);
        ProductionKeycloak.Record("schema: sub-flow requirements " + requirements.Replace('\n', ' '));
    }
}
