using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Testcontainers.Keycloak;

namespace Decisya.Identity.Tests;

/// <summary>
/// G4-17-09 (G3 T-14): a behavioural proof, on top of the configuration-only assertion in
/// <see cref="RealmConfigurationTests"/>, that a user cannot change their own
/// <c>tenant_id</c> through the built-in <c>account-console</c> client's account REST API.
/// Runs in its own container, never the shared <see cref="KeycloakRealmFixture"/> instance,
/// because the whole point of this test is to attempt (and expect to fail) a state change.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TenantSelfEditTests : IAsyncDisposable
{
    private KeycloakContainer? _container;

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task Dev_alice_cannot_change_her_own_tenant_id_through_the_account_console()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var clientSecret = KeycloakRealmFixture.GenerateHex(32);
        var devUserPassword = KeycloakRealmFixture.GenerateHex(20);
        var adminPassword = KeycloakRealmFixture.GenerateHex(24);

        _container = KeycloakRealmFixture.BuildContainer(clientSecret, devUserPassword, adminPassword);
        await _container.StartAsync(cancellationToken);
        var baseAddress = _container.GetBaseAddress();

        var adminToken = await KeycloakRealmFixture.GetAdminAccessTokenAsync(baseAddress, "admin", adminPassword, cancellationToken);
        using var adminClient = new HttpClient { BaseAddress = new Uri(baseAddress) };
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var originalTenantId = await GetTenantIdAttributeAsync(adminClient, "dev-alice", cancellationToken);
        var bobsTenantId = await GetTenantIdAttributeAsync(adminClient, "dev-bob", cancellationToken);

        // The built-in account-console client is not defined in decisya-realm.json, so
        // Keycloak generates it with its own defaults; read its registered redirect URI
        // back rather than guessing the base-path pattern.
        var accountConsole = await GetSingleClientAsync(adminClient, "account-console", cancellationToken);
        var redirectUri = accountConsole.GetProperty("redirectUris").EnumerateArray()
            .Select(e => e.GetString()!)
            .First(uri => !uri.Contains('*', StringComparison.Ordinal));

        using var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() };
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri(baseAddress) };

        var (verifier, challenge) = OidcTestHelpers.GeneratePkce();
        var state = Guid.NewGuid().ToString("N");

        var authorizeUrl =
            "/realms/decisya/protocol/openid-connect/auth?client_id=account-console&response_type=code" +
            "&scope=openid&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
            "&state=" + state + "&code_challenge=" + challenge + "&code_challenge_method=S256";

        using var authorizeResponse = await httpClient.GetAsync(authorizeUrl, cancellationToken);
        var html = await authorizeResponse.Content.ReadAsStringAsync(cancellationToken);
        var formAction = OidcTestHelpers.ExtractLoginFormAction(html);

        using var loginResponse = await httpClient.PostAsync(
            formAction,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "dev-alice",
                ["password"] = devUserPassword,
            }),
            cancellationToken);

        var location = loginResponse.Headers.Location
            ?? throw new InvalidOperationException("The account-console login did not redirect back.");
        var code = OidcTestHelpers.ParseQuery(location.Query)["code"];

        using var tokenResponse = await httpClient.PostAsync(
            "/realms/decisya/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = "account-console",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = verifier,
            }),
            cancellationToken);
        tokenResponse.EnsureSuccessStatusCode();

        using var tokenDocument = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(cancellationToken));
        var accessToken = tokenDocument.RootElement.GetProperty("access_token").GetString()!;

        using var accountClient = new HttpClient { BaseAddress = new Uri(baseAddress) };
        accountClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // Attempt 1: try to overwrite tenant_id with dev-bob's value through the managed attribute.
        var selfEditPayload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["username"] = "dev-alice",
            ["email"] = "alice@decisya.test",
            ["attributes"] = new Dictionary<string, string[]> { ["tenant_id"] = [bobsTenantId] },
        });

        using var selfEditResponse = await accountClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/realms/decisya/account/")
            {
                Content = new StringContent(selfEditPayload, Encoding.UTF8, "application/json"),
            },
            cancellationToken);

        // Either a 4xx (edit refused), or a 2xx that leaves the attribute unchanged
        // server-side (Keycloak silently drops an attribute the caller has no edit
        // permission on rather than erroring) both satisfy the requirement.
        if ((int)selfEditResponse.StatusCode < 400)
        {
            selfEditResponse.EnsureSuccessStatusCode();
        }

        var afterFirstAttempt = await GetTenantIdAttributeAsync(adminClient, "dev-alice", cancellationToken);
        afterFirstAttempt.Should().Be(originalTenantId, "the self-edit attempt must never change dev-alice's tenant_id");

        // Attempt 2: try to smuggle the same value in through an unmanaged attribute name
        // (camelCase "tenantId"), which unmanagedAttributePolicy: disabled must also refuse.
        var unmanagedAttemptPayload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["username"] = "dev-alice",
            ["email"] = "alice@decisya.test",
            ["attributes"] = new Dictionary<string, string[]> { ["tenantId"] = [bobsTenantId] },
        });

        using var unmanagedAttemptResponse = await accountClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/realms/decisya/account/")
            {
                Content = new StringContent(unmanagedAttemptPayload, Encoding.UTF8, "application/json"),
            },
            cancellationToken);
        _ = unmanagedAttemptResponse.StatusCode;

        var afterUnmanagedAttempt = await GetUserRepresentationAsync(adminClient, "dev-alice", cancellationToken);
        afterUnmanagedAttempt.TryGetProperty("attributes", out var attributesAfter).Should().BeTrue();
        attributesAfter.TryGetProperty("tenantId", out _).Should().BeFalse(
            "the unmanaged attribute name must never be created (unmanagedAttributePolicy stays disabled)");
        attributesAfter.GetProperty("tenant_id").EnumerateArray().Single().GetString().Should().Be(originalTenantId);
    }

    private static async Task<string> GetTenantIdAttributeAsync(HttpClient adminClient, string username, CancellationToken cancellationToken)
    {
        var user = await GetUserRepresentationAsync(adminClient, username, cancellationToken);
        return user.GetProperty("attributes").GetProperty("tenant_id").EnumerateArray().Single().GetString()!;
    }

    private static async Task<JsonElement> GetUserRepresentationAsync(HttpClient adminClient, string username, CancellationToken cancellationToken)
    {
        using var response = await adminClient.GetAsync($"/admin/realms/decisya/users?username={username}&exact=true", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.EnumerateArray().Single().Clone();
    }

    private static async Task<JsonElement> GetSingleClientAsync(HttpClient adminClient, string clientId, CancellationToken cancellationToken)
    {
        using var response = await adminClient.GetAsync($"/admin/realms/decisya/clients?clientId={clientId}", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.EnumerateArray().Single().Clone();
    }
}
