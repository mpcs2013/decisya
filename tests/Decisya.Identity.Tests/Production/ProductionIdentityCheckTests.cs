using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Decisya.Identity.Tests;

/// <summary>
/// Issue #121, G2 D5 and G3 G4-121-03 (d), G4-121-05 (a): <c>deploy/keycloak/production/identity-check.sql</c>
/// executed against a real Keycloak database, the way <c>stackctl.py verify</c> runs it (psql, the
/// SQL on stdin, <c>-At</c>). A Keycloak bump that renames a column fails here, in CI.
/// </summary>
[Trait("Category", "Integration")]
[Collection(ProductionKeycloakDefinition.Name)]
public sealed class ProductionIdentityCheckTests
{
    private readonly ProductionKeycloak _stack;

    public ProductionIdentityCheckTests(ProductionKeycloak stack)
    {
        _stack = stack;
    }

    [Fact]
    public async Task Right_after_the_import_the_check_reports_a_healthy_realm_with_zero_users()
    {
        var ct = TestContext.Current.CancellationToken;
        await _stack.EnsureStartedAsync(ct);

        _stack.IdentityCheckError.Should().BeNull();
        var check = _stack.IdentityCheckRightAfterImport;

        check["realm_present"].Should().Be("true");
        check["ssl_required_all"].Should().Be("true");
        check["events_enabled"].Should().Be("true");
        check["admin_events_enabled"].Should().Be("true");
        check["admin_events_details_off"].Should().Be("true");
        check["events_expiration_ok"].Should().Be("true");
        check["admin_events_expiration_ok"].Should().Be("true");
        check["jboss_logging_listener"].Should().Be("false");
        check["browser_flow_ok"].Should().Be("true");
        check["level_2_admin_conditional"].Should().Be("true");
        check["level_2_tenant_conditional"].Should().Be("false", "the C-02 tenant switch is off in Phase 0");
        check["password_policy_ok"].Should().Be("true");
        check["breached_list_in_policy"].Should().Be("false");
        check["users_total"].Should().Be("0");
        check["users_without_synthetic"].Should().Be("0");
        check["bff_secret_unresolved"].Should().Be("false");
        check["bff_secret_short"].Should().Be("false");
        check["bff_redirect_unresolved"].Should().Be("false");
        check["bff_logout_uris_unresolved"].Should().Be("false");

        // The bootstrap admin has no OTP: the count is what stackctl turns into a failure once the
        // bootstrap secret is retired (G4-121-05 a).
        int.Parse(check["master_users_without_otp"], System.Globalization.CultureInfo.InvariantCulture).Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task The_output_holds_only_counts_and_booleans_never_a_name_or_an_id()
    {
        var ct = TestContext.Current.CancellationToken;
        await _stack.EnsureStartedAsync(ct);
        var user = await _stack.CreateUserAsync(ProductionKeycloak.TenantRole, withTotp: false, ct);
        try
        {
            var check = await _stack.RunIdentityCheckAsync(ct);

            check.Keys.Should().OnlyContain(key => key.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'));
            check.Values.Should().OnlyContain(value => value == "true" || value == "false" || value.All(char.IsAsciiDigit));
            check.Values.Should().NotContain(value => value.Contains(user.Username, StringComparison.Ordinal));
        }
        finally
        {
            await _stack.DeleteUserAsync(user.Username, ct);
        }
    }

    [Fact]
    public async Task A_synthetic_user_is_counted_and_a_user_without_the_attribute_is_flagged()
    {
        var ct = TestContext.Current.CancellationToken;
        await _stack.EnsureStartedAsync(ct);

        var before = await _stack.RunIdentityCheckAsync(ct);
        var synthetic = await _stack.CreateUserAsync(ProductionKeycloak.TenantRole, withTotp: false, ct);
        ProductionKeycloak.TestUser? plain = null;
        try
        {
            var withSynthetic = await _stack.RunIdentityCheckAsync(ct);
            Delta(withSynthetic, before, "users_total").Should().Be(1);
            Delta(withSynthetic, before, "users_without_synthetic").Should().Be(0);

            plain = await _stack.CreateUserAsync(ProductionKeycloak.TenantRole, withTotp: false, ct, synthetic: false);
            var withPlain = await _stack.RunIdentityCheckAsync(ct);
            Delta(withPlain, before, "users_total").Should().Be(2);
            Delta(withPlain, before, "users_without_synthetic").Should().Be(1);
        }
        finally
        {
            await _stack.DeleteUserAsync(synthetic.Username, ct);
            if (plain is not null)
            {
                await _stack.DeleteUserAsync(plain.Username, ct);
            }
        }

        var after = await _stack.RunIdentityCheckAsync(ct);
        after["users_without_synthetic"].Should().Be(before["users_without_synthetic"]);
    }

    [Fact]
    public async Task A_master_realm_service_account_client_without_otp_is_counted()
    {
        var ct = TestContext.Current.CancellationToken;
        await _stack.EnsureStartedAsync(ct);
        using var admin = await _stack.AdminClientAsync(ct);

        var before = await _stack.RunIdentityCheckAsync(ct);
        var clientId = "t-sa-" + Guid.NewGuid().ToString("N")[..12];
        string? uuid = null;
        try
        {
            using var create = await admin.PostAsJsonAsync(
                "/admin/realms/master/clients",
                new
                {
                    clientId,
                    enabled = true,
                    publicClient = false,
                    serviceAccountsEnabled = true,
                    standardFlowEnabled = false,
                    directAccessGrantsEnabled = false,
                },
                ct);
            create.IsSuccessStatusCode.Should().BeTrue(
                $"the admin API answered {(int)create.StatusCode}: {ProductionKeycloak.Truncate(await create.Content.ReadAsStringAsync(ct), 300)}");

            var clients = await ProductionKeycloak.GetJsonAsync(admin, $"/admin/realms/master/clients?clientId={clientId}", ct);
            uuid = clients.EnumerateArray().Single().GetProperty("id").GetString();

            // The service-account user exists in the master realm and has no OTP credential.
            var withClient = await _stack.RunIdentityCheckAsync(ct);
            Delta(withClient, before, "master_users_without_otp").Should().Be(1);
        }
        finally
        {
            if (uuid is not null)
            {
                using var delete = await admin.DeleteAsync($"/admin/realms/master/clients/{uuid}", ct);
                delete.IsSuccessStatusCode.Should().BeTrue();
            }
        }

        var after = await _stack.RunIdentityCheckAsync(ct);
        after["master_users_without_otp"].Should().Be(before["master_users_without_otp"]);
    }

    [Fact]
    public async Task A_wildcard_in_a_redirect_uri_or_in_the_post_logout_uris_is_reported_and_reported_clean_again()
    {
        var ct = TestContext.Current.CancellationToken;
        await _stack.EnsureStartedAsync(ct);
        using var admin = await _stack.AdminClientAsync(ct);

        var clients = await ProductionKeycloak.GetJsonAsync(admin, $"/admin/realms/{ProductionKeycloak.Realm}/clients?clientId=decisya-bff", ct);
        var original = JsonNode.Parse(clients.EnumerateArray().Single().GetRawText())!.AsObject();
        var uuid = original["id"]!.GetValue<string>();
        var path = $"/admin/realms/{ProductionKeycloak.Realm}/clients/{uuid}";

        var tampered = JsonNode.Parse(original.ToJsonString())!.AsObject();
        tampered["redirectUris"] = new JsonArray("https://example.invalid/*");
        tampered["attributes"]!.AsObject()["post.logout.redirect.uris"] = "https://example.invalid/*";

        try
        {
            using var put = await admin.PutAsJsonAsync(path, tampered, ct);
            put.IsSuccessStatusCode.Should().BeTrue(
                $"the admin API answered {(int)put.StatusCode}: {ProductionKeycloak.Truncate(await put.Content.ReadAsStringAsync(ct), 300)}");

            var check = await _stack.RunIdentityCheckAsync(ct);
            check["bff_redirect_unresolved"].Should().Be("true");
            check["bff_logout_uris_unresolved"].Should().Be("true");
        }
        finally
        {
            using var restore = await admin.PutAsJsonAsync(path, original, ct);
            restore.IsSuccessStatusCode.Should().BeTrue();
        }

        var after = await _stack.RunIdentityCheckAsync(ct);
        after["bff_redirect_unresolved"].Should().Be("false");
        after["bff_logout_uris_unresolved"].Should().Be("false");
    }

    [Fact]
    public async Task Turning_the_tenant_switch_on_is_reported_as_the_c02_state_and_off_again()
    {
        var ct = TestContext.Current.CancellationToken;
        await _stack.EnsureStartedAsync(ct);
        using var admin = await _stack.AdminClientAsync(ct);

        var executions = await ProductionKeycloak.GetJsonAsync(
            admin, $"/admin/realms/{ProductionKeycloak.Realm}/authentication/flows/decisya-forms/executions", ct);
        var tenant = executions.EnumerateArray().Single(e =>
            e.TryGetProperty("authenticationFlow", out var f) && f.GetBoolean() && e.GetProperty("displayName").GetString() == "level-2-tenant");
        var node = JsonNode.Parse(tenant.GetRawText())!.AsObject();
        var updatePath = $"/admin/realms/{ProductionKeycloak.Realm}/authentication/flows/decisya-forms/executions";

        try
        {
            node["requirement"] = "CONDITIONAL";
            using var on = await admin.PutAsJsonAsync(updatePath, node, ct);
            on.IsSuccessStatusCode.Should().BeTrue();
            (await _stack.RunIdentityCheckAsync(ct))["level_2_tenant_conditional"].Should().Be("true");
        }
        finally
        {
            node["requirement"] = "DISABLED";
            using var off = await admin.PutAsJsonAsync(updatePath, node, ct);
            off.IsSuccessStatusCode.Should().BeTrue();
        }

        (await _stack.RunIdentityCheckAsync(ct))["level_2_tenant_conditional"].Should().Be("false");
    }

    private static int Delta(IReadOnlyDictionary<string, string> now, IReadOnlyDictionary<string, string> before, string key) =>
        int.Parse(now[key], System.Globalization.CultureInfo.InvariantCulture)
        - int.Parse(before[key], System.Globalization.CultureInfo.InvariantCulture);
}
