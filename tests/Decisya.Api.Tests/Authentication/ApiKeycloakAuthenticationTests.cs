using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Decisya.Identity.Tests;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// Harness B (G2): the same negative cases as harness A, proven against a real Keycloak
/// container and real tokens from the decisya realm's own login flow, rather than
/// hand-crafted ones — an ID token, a refresh token, and HS256/alg=none built over
/// <c>dev-alice</c>'s own genuine claims.
/// </summary>
[Trait("Category", "Integration")]
public class ApiKeycloakAuthenticationTests
{
    private const string ClientId = "decisya-bff";
    private const string RegisteredRedirectUri = "https://localhost:7200/signin-oidc";
    private const string DevAliceTenantId = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    private readonly KeycloakApiFixture _fixture;

    public ApiKeycloakAuthenticationTests(KeycloakApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Dev_alice_and_dev_admin_access_tokens_authenticate_with_the_expected_claims()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _fixture.EnsureStartedAsync(cancellationToken);
        await using var apiFactory = KeycloakBackedApiFactory.Create(_fixture);
        using var apiClient = apiFactory.CreateClient();

        var aliceTokens = await LoginAsync("dev-alice", cancellationToken);
        using var aliceResponse = await SendWhoAmIAsync(apiClient, aliceTokens.AccessToken, cancellationToken);
        aliceResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var aliceBody = JsonDocument.Parse(await aliceResponse.Content.ReadAsStringAsync(cancellationToken));
        aliceBody.RootElement.GetProperty("tenantId").GetString().Should().Be(DevAliceTenantId);

        var adminTokens = await LoginAsync("dev-admin", cancellationToken);
        using var adminResponse = await SendWhoAmIAsync(apiClient, adminTokens.AccessToken, cancellationToken);
        adminResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var adminBody = JsonDocument.Parse(await adminResponse.Content.ReadAsStringAsync(cancellationToken));
        adminBody.RootElement.TryGetProperty("tenantId", out _).Should().BeFalse();
    }

    [Fact]
    public async Task An_id_token_is_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _fixture.EnsureStartedAsync(cancellationToken);
        await using var apiFactory = KeycloakBackedApiFactory.Create(_fixture);
        using var apiClient = apiFactory.CreateClient();

        var tokens = await LoginAsync("dev-alice", cancellationToken);

        using var response = await SendWhoAmIAsync(apiClient, tokens.IdToken, cancellationToken);
        await AssertBareRejectionAsync(response, cancellationToken);
    }

    [Fact]
    public async Task A_refresh_token_is_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _fixture.EnsureStartedAsync(cancellationToken);
        await using var apiFactory = KeycloakBackedApiFactory.Create(_fixture);
        using var apiClient = apiFactory.CreateClient();

        var tokens = await LoginAsync("dev-alice", cancellationToken);
        tokens.RefreshToken.Should().NotBeNull("the realm should grant decisya-bff a refresh token");

        using var response = await SendWhoAmIAsync(apiClient, tokens.RefreshToken!, cancellationToken);
        await AssertBareRejectionAsync(response, cancellationToken);
    }

    [Fact]
    public async Task Alg_none_and_HS256_keyed_with_the_realms_own_public_key_are_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _fixture.EnsureStartedAsync(cancellationToken);
        await using var apiFactory = KeycloakBackedApiFactory.Create(_fixture);
        using var apiClient = apiFactory.CreateClient();

        var tokens = await LoginAsync("dev-alice", cancellationToken);
        var claims = ToClaimsDictionary(JwtHelper.DecodePayload(tokens.AccessToken));

        var algNoneToken = TestTokenIssuer.BuildAlgNoneToken(claims);
        using var algNoneResponse = await SendWhoAmIAsync(apiClient, algNoneToken, cancellationToken);
        await AssertBareRejectionAsync(algNoneResponse, cancellationToken);

        using var rsa = await GetRealmRsaPublicKeyAsync(cancellationToken);
        var der = rsa.ExportSubjectPublicKeyInfo();
        var pem = PemEncode(der);

        var hs256WithDerToken = TestTokenIssuer.BuildHs256Token(der, claims);
        using var hs256DerResponse = await SendWhoAmIAsync(apiClient, hs256WithDerToken, cancellationToken);
        await AssertBareRejectionAsync(hs256DerResponse, cancellationToken);

        var hs256WithPemToken = TestTokenIssuer.BuildHs256Token(Encoding.ASCII.GetBytes(pem), claims);
        using var hs256PemResponse = await SendWhoAmIAsync(apiClient, hs256WithPemToken, cancellationToken);
        await AssertBareRejectionAsync(hs256PemResponse, cancellationToken);
    }

    private static async Task<HttpResponseMessage> SendWhoAmIAsync(HttpClient client, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task AssertBareRejectionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Should().ContainSingle();
        response.Headers.WwwAuthenticate.Single().Scheme.Should().Be("Bearer");
        (await response.Content.ReadAsStringAsync(cancellationToken)).Should().BeEmpty();
    }

    private static Dictionary<string, object> ToClaimsDictionary(JsonElement payload)
    {
        var claims = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var property in payload.EnumerateObject())
        {
            claims[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString()!,
                JsonValueKind.Number => property.Value.GetInt64(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => property.Value.GetRawText(),
            };
        }

        return claims;
    }

    private async Task<RSA> GetRealmRsaPublicKeyAsync(CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        using var discoveryResponse = await httpClient.GetAsync(
            "/realms/decisya/.well-known/openid-configuration", cancellationToken);
        discoveryResponse.EnsureSuccessStatusCode();
        using var discoveryDocument = JsonDocument.Parse(await discoveryResponse.Content.ReadAsStringAsync(cancellationToken));
        var jwksUri = discoveryDocument.RootElement.GetProperty("jwks_uri").GetString()!;

        using var jwksResponse = await httpClient.GetAsync(new Uri(jwksUri).PathAndQuery, cancellationToken);
        jwksResponse.EnsureSuccessStatusCode();
        using var jwks = JsonDocument.Parse(await jwksResponse.Content.ReadAsStringAsync(cancellationToken));

        var signingKey = jwks.RootElement.GetProperty("keys").EnumerateArray()
            .First(key => key.GetProperty("kty").GetString() == "RSA"
                && (!key.TryGetProperty("use", out var use) || use.GetString() == "sig"));

        var modulus = Base64Url.DecodeFromChars(signingKey.GetProperty("n").GetString()!);
        var exponent = Base64Url.DecodeFromChars(signingKey.GetProperty("e").GetString()!);

        return RSA.Create(new RSAParameters { Modulus = modulus, Exponent = exponent });
    }

    private static string PemEncode(byte[] der)
    {
        var base64 = Convert.ToBase64String(der);
        var builder = new StringBuilder("-----BEGIN PUBLIC KEY-----\n");
        for (var i = 0; i < base64.Length; i += 64)
        {
            builder.Append(base64, i, Math.Min(64, base64.Length - i)).Append('\n');
        }

        builder.Append("-----END PUBLIC KEY-----\n");
        return builder.ToString();
    }

    private async Task<(string AccessToken, string IdToken, string? RefreshToken)> LoginAsync(
        string username, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient(new SecureCookieRelayHandler()) { BaseAddress = new Uri(_fixture.BaseAddress) };

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
                ["password"] = _fixture.DevUserPassword,
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
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{ClientId}:{_fixture.ClientSecret}")));
        using var tokenResponse = await httpClient.SendAsync(tokenRequest, cancellationToken);
        tokenResponse.EnsureSuccessStatusCode();

        using var tokenDocument = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(cancellationToken));
        var accessToken = tokenDocument.RootElement.GetProperty("access_token").GetString()!;
        var idToken = tokenDocument.RootElement.GetProperty("id_token").GetString()!;
        var refreshToken = tokenDocument.RootElement.TryGetProperty("refresh_token", out var refreshTokenElement)
            ? refreshTokenElement.GetString()
            : null;

        return (accessToken, idToken, refreshToken);
    }
}
