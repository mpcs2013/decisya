using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Decisya.Identity.Tests;
using Npgsql;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// Issue #25, G1 Story 6 (second half, with Postgres): the API accepts dev-admin's real Keycloak access token
/// on the admin endpoints and refuses dev-alice's real token with the generic 403. Real Keycloak (the decisya
/// realm file, with the <c>realm-roles-access-token</c> mapper), the real Api host, and a real migrated
/// Postgres. The BFF half (cookie, antiforgery, forwarding) is proven in <c>Decisya.Bff.Tests</c>.
/// </summary>
[Trait("Category", "Integration")]
[Collection("KeycloakLogins")]
public class AdminKeycloakTests
{
    private const string ClientId = "decisya-bff";
    private const string RegisteredRedirectUri = "https://localhost:7200/signin-oidc";
    private const string DevAliceTenantId = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    private readonly KeycloakApiFixture _keycloak;
    private readonly TenancyDatabaseFixture _database;

    public AdminKeycloakTests(KeycloakApiFixture keycloak, TenancyDatabaseFixture database)
    {
        _keycloak = keycloak;
        _database = database;
    }

    [Fact]
    public async Task Dev_admins_real_access_token_passes_the_admin_policy_and_dev_alices_gets_403()
    {
        var ct = TestContext.Current.CancellationToken;
        await _keycloak.EnsureStartedAsync(ct);
        var connectionString = await _database.CreateFullyMigratedDatabaseAsync(ct);
        await using var factory = KeycloakBackedApiFactory.Create(_keycloak, connectionString, connectionString);
        using var client = factory.CreateClient();

        var alice = await LoginAsync("dev-alice", ct);
        var admin = await LoginAsync("dev-admin", ct);

        // dev-alice signs in once, which creates her tenant (JIT, #21).
        using (var me = await SendAsync(client, "GET", "/api/tenancy/me", alice))
        {
            me.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var trial = $"/api/admin/tenants/{DevAliceTenantId}/trial";
        var grant = $"/api/admin/tenants/{DevAliceTenantId}/overrides/forecasting.scenarios";

        // dev-alice: 403 on every verb, the generic body, and nothing written.
        foreach (var (method, path, body) in new (string, string, string?)[]
        {
            ("POST", trial, null),
            ("PUT", grant, """{"reason":"alice grants herself"}"""),
            ("DELETE", grant, null),
        })
        {
            using var response = await SendAsync(client, method, path, alice, body);

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{method} as dev-alice");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            json.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["type", "title", "status", "traceId"]);
        }

        (await CountAsync(connectionString, "SELECT count(*) FROM entitlements.trial_grants")).Should().Be(0);
        (await CountAsync(connectionString, "SELECT count(*) FROM entitlements.feature_overrides")).Should().Be(0);
        (await CountAsync(connectionString, "SELECT count(*) FROM audit.audit_records")).Should().Be(0);

        // dev-admin: the policy passes, the commands run, and every success is audited with the admin's sub.
        foreach (var (method, path, body) in new (string, string, string?)[]
        {
            ("POST", trial, null),
            ("PUT", grant, """{"reason":"Design-partner pilot"}"""),
            ("DELETE", grant, null),
        })
        {
            using var response = await SendAsync(client, method, path, admin, body);

            response.StatusCode.Should().Be(HttpStatusCode.NoContent, $"{method} as dev-admin");
        }

        var adminSub = SubOf(admin);
        adminSub.Should().NotBe(SubOf(alice));
        (await CountAsync(connectionString, "SELECT count(*) FROM entitlements.trial_grants")).Should().Be(1);
        (await CountAsync(connectionString, "SELECT count(*) FROM audit.audit_records")).Should().Be(3);
        (await CountAsync(connectionString, $"SELECT count(*) FROM audit.audit_records WHERE actor_user_id = '{adminSub}'")).Should().Be(3);
    }

    private static string SubOf(string accessToken)
    {
        var payload = accessToken.Split('.')[1];
        var bytes = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(payload);
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.GetProperty("sub").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path, string token, string? body = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<long> CountAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
#pragma warning disable CA2100 // every call site passes a literal, or a Keycloak sub (a GUID) interpolated into one.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<string> LoginAsync(string username, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient(new SecureCookieRelayHandler()) { BaseAddress = new Uri(_keycloak.BaseAddress) };

        var (verifier, challenge) = OidcTestHelpers.GeneratePkce();
        var state = Guid.NewGuid().ToString("N");
        var nonce = Guid.NewGuid().ToString("N");

        using var authorizeResponse = await httpClient.GetAsync(
            "/realms/decisya/protocol/openid-connect/auth"
            + $"?client_id={ClientId}&response_type=code&scope=openid"
            + $"&redirect_uri={Uri.EscapeDataString(RegisteredRedirectUri)}"
            + $"&state={state}&nonce={nonce}&code_challenge={challenge}&code_challenge_method=S256",
            cancellationToken);
        var html = await authorizeResponse.Content.ReadAsStringAsync(cancellationToken);
        var formAction = OidcTestHelpers.ExtractLoginFormAction(html);

        using var loginResponse = await httpClient.PostAsync(
            formAction,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = username,
                ["password"] = _keycloak.DevUserPassword,
            }),
            cancellationToken);
        loginResponse.StatusCode.Should().Be(HttpStatusCode.Found, $"{username}'s login should redirect with a code");
        var redirectQuery = OidcTestHelpers.ParseQuery(loginResponse.Headers.Location!.Query);

        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "/realms/decisya/protocol/openid-connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = redirectQuery["code"],
                ["redirect_uri"] = RegisteredRedirectUri,
                ["code_verifier"] = verifier,
            }),
        };
        tokenRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{ClientId}:{_keycloak.ClientSecret}")));
        using var tokenResponse = await httpClient.SendAsync(tokenRequest, cancellationToken);
        tokenResponse.EnsureSuccessStatusCode();

        using var tokenDocument = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(cancellationToken));
        return tokenDocument.RootElement.GetProperty("access_token").GetString()!;
    }
}
