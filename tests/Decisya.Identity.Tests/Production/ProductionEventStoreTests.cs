using System.Net.Http.Json;
using System.Text.Json;

namespace Decisya.Identity.Tests;

/// <summary>
/// Issue #121, Story 6 (C-10) and NFR-45/NFR-46, against the production realm in the pinned Keycloak:
/// a failed and then a successful sign-in each leave exactly one stored event, and no stored login or
/// admin event holds a password, a token, a TOTP secret, the authorization code or the client secret.
/// Usernames and client IPs in the store are the accepted residual R-1 and are not findings.
/// </summary>
[Trait("Category", "Integration")]
[Collection(ProductionKeycloakDefinition.Name)]
public sealed class ProductionEventStoreTests
{
    private readonly ProductionKeycloak _stack;

    public ProductionEventStoreTests(ProductionKeycloak stack)
    {
        _stack = stack;
    }

    [Fact]
    public async Task A_failed_and_a_successful_keycloak_login_are_recorded_and_no_event_holds_a_secret()
    {
        var ct = TestContext.Current.CancellationToken;
        await _stack.EnsureStartedAsync(ct);
        var user = await _stack.CreateUserAsync(ProductionKeycloak.AdminRole, withTotp: true, ct);
        var wrongPassword = ProductionKeycloak.Hex(24);

        var failed = await _stack.LoginAsync(user.Username, wrongPassword, "2", null, ct, expectFailure: true);
        failed.Code.Should().BeEmpty("a wrong password yields no authorization code");
        var login = await _stack.LoginAsync(user.Username, user.Password, "2", user.TotpSecret, ct);
        var tokens = await _stack.ExchangeAsync(login, ct);

        using var admin = await _stack.AdminClientAsync(ct);
        var users = await ProductionKeycloak.GetJsonAsync(
            admin, $"/admin/realms/{ProductionKeycloak.Realm}/users?username={user.Username}&exact=true", ct);
        var userId = users[0].GetProperty("id").GetString()!;

        var raw = await ReadEventsAsync(admin, userId, ct);
        using var document = JsonDocument.Parse(raw);
        var types = document.RootElement.EnumerateArray().Select(e => e.GetProperty("type").GetString()).ToList();
        types.Count(t => t == "LOGIN_ERROR").Should().Be(1, "the wrong password is one stored LOGIN_ERROR");
        types.Count(t => t == "LOGIN").Should().Be(1, "the sign-in that followed is one stored LOGIN");
        types.Should().OnlyContain(t => t == "LOGIN" || t == "LOGIN_ERROR" || t == "UPDATE_TOTP" || t == "REMOVE_TOTP");

        AssertNoSecret(raw, wrongPassword, user.Password, user.TotpSecret!, login.Code, _stack.ClientSecret,
            tokens.AccessToken, tokens.RefreshToken, tokens.IdToken);
    }

    [Fact]
    public async Task Admin_events_are_stored_without_a_representation_and_hold_no_credential()
    {
        var ct = TestContext.Current.CancellationToken;
        await _stack.EnsureStartedAsync(ct);
        var username = "t-" + ProductionKeycloak.Hex(12);
        var password = ProductionKeycloak.Hex(24);

        using var admin = await _stack.AdminClientAsync(ct);
        using var create = await admin.PostAsJsonAsync(
            $"/admin/realms/{ProductionKeycloak.Realm}/users",
            new
            {
                username,
                enabled = true,
                attributes = new Dictionary<string, string[]> { ["synthetic"] = ["true"] },
                credentials = new[] { new { type = "password", value = password, temporary = false } },
            },
            ct);
        create.IsSuccessStatusCode.Should().BeTrue($"the user creation answered {(int)create.StatusCode}");

        using var response = await admin.GetAsync(
            $"/admin/realms/{ProductionKeycloak.Realm}/admin-events?resourceTypes=USER&operationTypes=CREATE", ct);
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(raw);

        document.RootElement.GetArrayLength().Should().BeGreaterThan(0, "admin events are enabled");
        document.RootElement.EnumerateArray().Select(e => e.TryGetProperty("representation", out var representation) ? representation.ToString() : null)
            .Should().OnlyContain(representation => representation == null, "adminEventsDetailsEnabled is false");
        AssertNoSecret(raw, password);

        // The deleted-user cleanup keeps the shared realm as it was for the identity-check tests.
        await _stack.DeleteUserAsync(username, ct);
    }

    private static async Task<string> ReadEventsAsync(HttpClient admin, string userId, CancellationToken ct)
    {
        // The store is written in the request's transaction; the short poll only guards against a slow commit.
        var path = $"/admin/realms/{ProductionKeycloak.Realm}/events?user={userId}&type=LOGIN&type=LOGIN_ERROR&max=50";
        var raw = "[]";
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var response = await admin.GetAsync(path, ct);
            response.EnsureSuccessStatusCode();
            raw = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.GetArrayLength() >= 2)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        return raw;
    }

    private static void AssertNoSecret(string storedEvents, params string[] secrets)
    {
        foreach (var secret in secrets)
        {
            storedEvents.Should().NotContain(secret, "a stored Keycloak event never holds a password, token, TOTP secret, code or client secret");
        }
    }
}
