using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Decisya.Identity.Tests;

/// <summary>
/// Story 6 (the Done-when) and G4-17-06/07/08: drives the real authorization-code + PKCE
/// flow with a plain <see cref="HttpClient"/> ("browser-shaped, no browser", G2) against the
/// shared fixture's Keycloak instance, and proves the negative cases named by G3.
/// </summary>
[Trait("Category", "Integration")]
public class BffLoginFlowTests
{
    private const string ClientId = "decisya-bff";
    private const string RegisteredRedirectUri = "https://localhost:7200/signin-oidc";

    private readonly KeycloakRealmFixture _fixture;

    public BffLoginFlowTests(KeycloakRealmFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task A_seeded_user_completes_the_hosted_login_form_and_receives_a_conformant_token_set()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _fixture.EnsureStartedAsync(cancellationToken);
        using var httpClient = CreateNonRedirectingClient();

        var (verifier, challenge) = OidcTestHelpers.GeneratePkce();
        var state = Guid.NewGuid().ToString("N");
        var nonce = Guid.NewGuid().ToString("N");

        using var authorizeResponse = await httpClient.GetAsync(
            BuildAuthorizeUrl(state, nonce, challenge, "S256", RegisteredRedirectUri, "openid"), cancellationToken);
        authorizeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await authorizeResponse.Content.ReadAsStringAsync(cancellationToken);
        html.Should().Contain("kc-form-login");
        html.Should().Contain("/realms/decisya/");
        html.Should().NotContain("/realms/master/");

        var formAction = OidcTestHelpers.ExtractLoginFormAction(html);

        using var loginResponse = await httpClient.PostAsync(
            formAction,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "dev-alice",
                ["password"] = _fixture.DevUserPassword,
            }),
            cancellationToken);

        loginResponse.StatusCode.Should().Be(HttpStatusCode.Found);
        var location = loginResponse.Headers.Location
            ?? throw new InvalidOperationException("The login form's response carried no Location header.");
        location.ToString().Should().StartWith(RegisteredRedirectUri);

        var redirectQuery = OidcTestHelpers.ParseQuery(location.Query);
        redirectQuery.Should().ContainKey("code");
        redirectQuery["state"].Should().Be(state);

        var tokenResponseBody = await ExchangeCodeAsync(
            httpClient, redirectQuery["code"], verifier, RegisteredRedirectUri, ClientId, _fixture.ClientSecret, cancellationToken);
        tokenResponseBody.StatusCode.Should().Be(HttpStatusCode.OK);

        using var tokenDocument = JsonDocument.Parse(tokenResponseBody.Body);
        var accessToken = tokenDocument.RootElement.GetProperty("access_token").GetString()!;
        var idToken = tokenDocument.RootElement.GetProperty("id_token").GetString()!;

        if (tokenDocument.RootElement.TryGetProperty("scope", out var scopeElement))
        {
            (scopeElement.GetString() ?? string.Empty).Should().NotContain("offline_access");
        }

        if (tokenDocument.RootElement.TryGetProperty("refresh_token", out var refreshTokenElement))
        {
            var refreshPayload = JwtHelper.DecodePayload(refreshTokenElement.GetString()!);
            if (refreshPayload.TryGetProperty("typ", out var refreshTypClaim))
            {
                refreshTypClaim.GetString().Should().NotBe("Offline");
            }
        }

        var jwksUri = await GetJwksUriAsync(httpClient, cancellationToken);
        await AssertTokenIsConformantAsync(httpClient, accessToken, jwksUri, cancellationToken);
        await AssertTokenIsConformantAsync(httpClient, idToken, jwksUri, cancellationToken);

        var accessPayload = JwtHelper.DecodePayload(accessToken);
        accessPayload.GetProperty("azp").GetString().Should().Be(ClientId);
        var audiences = ReadAudiences(accessPayload);
        audiences.Should().Contain("decisya-api");
        audiences.Should().NotContain("account");
        audiences.Should().NotContain("realm-management");

        accessPayload.GetProperty("tenant_id").GetString().Should().Be("7c9e6679-7425-40de-944b-e07fc1f90ae7");

        // #25 G2 D1: the flat roles claim in the access token. dev-alice is a tenant user only.
        ReadStringArray(accessPayload, "roles").Should().BeEquivalentTo(["tenant-user"]);
        ReadStringArray(accessPayload, "roles").Should().NotContain("platform-admin");
        var idPayload = JwtHelper.DecodePayload(idToken);
        idPayload.GetProperty("tenant_id").GetString().Should().Be("7c9e6679-7425-40de-944b-e07fc1f90ae7");
    }

    [Fact]
    public async Task A_platform_admin_login_carries_no_tenant_id_claim_in_either_token()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _fixture.EnsureStartedAsync(cancellationToken);
        using var httpClient = CreateNonRedirectingClient();

        var (verifier, challenge) = OidcTestHelpers.GeneratePkce();
        var state = Guid.NewGuid().ToString("N");
        var nonce = Guid.NewGuid().ToString("N");

        using var authorizeResponse = await httpClient.GetAsync(
            BuildAuthorizeUrl(state, nonce, challenge, "S256", RegisteredRedirectUri, "openid"), cancellationToken);
        var formAction = OidcTestHelpers.ExtractLoginFormAction(await authorizeResponse.Content.ReadAsStringAsync(cancellationToken));

        using var loginResponse = await httpClient.PostAsync(
            formAction,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "dev-admin",
                ["password"] = _fixture.DevUserPassword,
            }),
            cancellationToken);

        // Fails fast and legibly (rather than a NullReferenceException on the next line) if
        // dev-admin's login itself was rejected.
        loginResponse.StatusCode.Should().Be(HttpStatusCode.Found, "dev-admin's login should redirect with a code");
        var location = loginResponse.Headers.Location!;
        var redirectQuery = OidcTestHelpers.ParseQuery(location.Query);

        var tokenResponseBody = await ExchangeCodeAsync(
            httpClient, redirectQuery["code"], verifier, RegisteredRedirectUri, ClientId, _fixture.ClientSecret, cancellationToken);
        using var tokenDocument = JsonDocument.Parse(tokenResponseBody.Body);
        var accessToken = tokenDocument.RootElement.GetProperty("access_token").GetString()!;
        var idToken = tokenDocument.RootElement.GetProperty("id_token").GetString()!;

        var accessPayload = JwtHelper.DecodePayload(accessToken);
        var idPayload = JwtHelper.DecodePayload(idToken);

        accessPayload.TryGetProperty("tenant_id", out _).Should().BeFalse("no fabricated tenant_id in the access token");
        idPayload.TryGetProperty("tenant_id", out _).Should().BeFalse("no fabricated tenant_id in the id token");

        // #25 G2 D1: dev-admin's access token carries exactly platform-admin in the flat roles claim.
        ReadStringArray(accessPayload, "roles").Should().BeEquivalentTo(["platform-admin"]);
    }

    [Theory]
    [InlineData("http://localhost:7200/signin-oidc")]
    [InlineData("https://localhost:7200/signin-oidc/x")]
    [InlineData("https://localhost:7200/signin-oidc?x=1")]
    [InlineData("https://attacker.test/signin-oidc")]
    public async Task An_authorization_request_with_an_unregistered_redirect_uri_gets_no_login_form(string redirectUri)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _fixture.EnsureStartedAsync(cancellationToken);
        using var httpClient = CreateNonRedirectingClient();
        var (_, challenge) = OidcTestHelpers.GeneratePkce();

        using var response = await httpClient.GetAsync(
            BuildAuthorizeUrl("state", "nonce", challenge, "S256", redirectUri, "openid"), cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        body.Should().NotContain("kc-form-login");
    }

    [Theory]
    [InlineData("token")]
    [InlineData("id_token")]
    public async Task An_authorization_request_for_an_implicit_response_type_gets_no_login_form(string responseType)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _fixture.EnsureStartedAsync(cancellationToken);
        using var httpClient = CreateNonRedirectingClient();
        var (_, challenge) = OidcTestHelpers.GeneratePkce();

        var url = BuildAuthorizeUrl("state", "nonce", challenge, "S256", RegisteredRedirectUri, "openid")
            .Replace("response_type=code", $"response_type={responseType}", StringComparison.Ordinal);

        using var response = await httpClient.GetAsync(url, cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        body.Should().NotContain("kc-form-login");
    }

    [Fact]
    public async Task An_authorization_request_with_plain_PKCE_or_no_challenge_gets_no_login_form()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _fixture.EnsureStartedAsync(cancellationToken);
        using var httpClient = CreateNonRedirectingClient();
        var (_, challenge) = OidcTestHelpers.GeneratePkce();

        using var plainResponse = await httpClient.GetAsync(
            BuildAuthorizeUrl("state", "nonce", challenge, "plain", RegisteredRedirectUri, "openid"), cancellationToken);
        (await plainResponse.Content.ReadAsStringAsync(cancellationToken)).Should().NotContain("kc-form-login");

        var noChallengeUrl =
            $"/realms/decisya/protocol/openid-connect/auth?client_id={ClientId}&response_type=code" +
            $"&scope=openid&redirect_uri={Uri.EscapeDataString(RegisteredRedirectUri)}&state=state";
        using var noChallengeResponse = await httpClient.GetAsync(noChallengeUrl, cancellationToken);
        (await noChallengeResponse.Content.ReadAsStringAsync(cancellationToken)).Should().NotContain("kc-form-login");
    }

    [Fact]
    public async Task Password_and_client_credentials_grants_are_rejected_on_decisya_bff()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _fixture.EnsureStartedAsync(cancellationToken);
        using var httpClient = CreateNonRedirectingClient();

        using var passwordGrantResponse = await PostWithBasicAuthAsync(
            httpClient, ClientId, _fixture.ClientSecret,
            new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = "dev-alice",
                ["password"] = _fixture.DevUserPassword,
            },
            cancellationToken);
        passwordGrantResponse.IsSuccessStatusCode.Should().BeFalse("directAccessGrantsEnabled is false on decisya-bff");

        using var clientCredentialsResponse = await PostWithBasicAuthAsync(
            httpClient, ClientId, _fixture.ClientSecret,
            new Dictionary<string, string> { ["grant_type"] = "client_credentials" },
            cancellationToken);
        clientCredentialsResponse.IsSuccessStatusCode.Should().BeFalse("serviceAccountsEnabled is false on decisya-bff");
    }

    [Fact]
    public async Task A_token_request_with_the_wrong_client_secret_is_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _fixture.EnsureStartedAsync(cancellationToken);
        using var httpClient = CreateNonRedirectingClient();

        using var response = await PostWithBasicAuthAsync(
            httpClient, ClientId, "wrong-" + _fixture.ClientSecret,
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = "irrelevant",
                ["redirect_uri"] = RegisteredRedirectUri,
            },
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_code_exchange_without_the_PKCE_verifier_fails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _fixture.EnsureStartedAsync(cancellationToken);
        using var httpClient = CreateNonRedirectingClient();

        var (_, challenge) = OidcTestHelpers.GeneratePkce();
        var state = Guid.NewGuid().ToString("N");
        using var authorizeResponse = await httpClient.GetAsync(
            BuildAuthorizeUrl(state, "nonce", challenge, "S256", RegisteredRedirectUri, "openid"), cancellationToken);
        var formAction = OidcTestHelpers.ExtractLoginFormAction(await authorizeResponse.Content.ReadAsStringAsync(cancellationToken));

        using var loginResponse = await httpClient.PostAsync(
            formAction,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "dev-bob",
                ["password"] = _fixture.DevUserPassword,
            }),
            cancellationToken);
        // See the note on A_platform_admin_login_carries_no_tenant_id_claim_in_either_token:
        // fails fast and legibly if dev-bob's login itself was rejected.
        loginResponse.StatusCode.Should().Be(HttpStatusCode.Found, "dev-bob's login should redirect with a code");
        var redirectQuery = OidcTestHelpers.ParseQuery(loginResponse.Headers.Location!.Query);

        using var tokenResponse = await PostWithBasicAuthAsync(
            httpClient, ClientId, _fixture.ClientSecret,
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = redirectQuery["code"],
                ["redirect_uri"] = RegisteredRedirectUri,
                // No code_verifier: the PKCE-required client must reject this.
            },
            cancellationToken);

        tokenResponse.IsSuccessStatusCode.Should().BeFalse();
    }

    [Fact]
    public async Task A_password_grant_against_admin_cli_in_the_decisya_realm_fails_or_yields_a_harmless_token()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _fixture.EnsureStartedAsync(cancellationToken);
        using var httpClient = CreateNonRedirectingClient();

        using var response = await httpClient.PostAsync(
            "/realms/decisya/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "admin-cli",
                ["username"] = "dev-alice",
                ["password"] = _fixture.DevUserPassword,
            }),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // admin-cli's directAccessGrantsEnabled: false (G3 change 5) rejects the grant.
            return;
        }

        // Fallback (G4-17-08): if Keycloak 26 ignored the built-in client's override, the
        // resulting token must still carry no decisya-api audience and no tenant_id.
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var accessToken = document.RootElement.GetProperty("access_token").GetString()!;
        var payload = JwtHelper.DecodePayload(accessToken);

        ReadAudiences(payload).Should().NotContain("decisya-api");
        payload.TryGetProperty("tenant_id", out _).Should().BeFalse();
    }

    private HttpClient CreateNonRedirectingClient()
    {
        // SecureCookieRelayHandler, not CookieContainer (G4 finding): Keycloak's
        // auth-session cookies are Secure-flagged even on this http:// Testcontainers
        // origin, and CookieContainer correctly refuses to re-send them there.
        return new HttpClient(new SecureCookieRelayHandler()) { BaseAddress = new Uri(_fixture.BaseAddress) };
    }

    private static string BuildAuthorizeUrl(
        string state, string nonce, string codeChallenge, string codeChallengeMethod, string redirectUri, string scope) =>
        "/realms/decisya/protocol/openid-connect/auth"
        + $"?client_id={ClientId}"
        + "&response_type=code"
        + $"&scope={Uri.EscapeDataString(scope)}"
        + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
        + $"&state={Uri.EscapeDataString(state)}"
        + $"&nonce={Uri.EscapeDataString(nonce)}"
        + $"&code_challenge={Uri.EscapeDataString(codeChallenge)}"
        + $"&code_challenge_method={Uri.EscapeDataString(codeChallengeMethod)}";

    private static async Task<HttpResponseMessage> PostWithBasicAuthAsync(
        HttpClient httpClient, string clientId, string clientSecret, Dictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/realms/decisya/protocol/openid-connect/token")
        {
            Content = new FormUrlEncodedContent(fields),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}")));

        return await httpClient.SendAsync(request, cancellationToken);
    }

    private static async Task<(HttpStatusCode StatusCode, string Body)> ExchangeCodeAsync(
        HttpClient httpClient, string code, string verifier, string redirectUri, string clientId, string clientSecret,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/realms/decisya/protocol/openid-connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = verifier,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}")));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return (response.StatusCode, body);
    }

    private static async Task<string> GetJwksUriAsync(HttpClient httpClient, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            "/realms/decisya/.well-known/openid-configuration", cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var issuer = document.RootElement.GetProperty("issuer").GetString()!;
        issuer.Should().EndWith("/realms/decisya");
        return document.RootElement.GetProperty("jwks_uri").GetString()!;
    }

    private static async Task AssertTokenIsConformantAsync(
        HttpClient httpClient, string jwt, string jwksUri, CancellationToken cancellationToken)
    {
        var header = JwtHelper.DecodeHeader(jwt);
        var alg = header.GetProperty("alg").GetString();
        alg.Should().BeOneOf("RS256", "ES256");

        var kid = header.GetProperty("kid").GetString();

        using var jwksResponse = await httpClient.GetAsync(new Uri(jwksUri).PathAndQuery, cancellationToken);
        jwksResponse.EnsureSuccessStatusCode();
        using var jwks = JsonDocument.Parse(await jwksResponse.Content.ReadAsStringAsync(cancellationToken));
        var matchingKey = jwks.RootElement.GetProperty("keys").EnumerateArray()
            .SingleOrDefault(k => k.GetProperty("kid").GetString() == kid);
        matchingKey.ValueKind.Should().NotBe(JsonValueKind.Undefined, $"kid '{kid}' should be published at {jwksUri}");
        matchingKey.GetProperty("kty").GetString().Should().BeOneOf("RSA", "EC");

        var payload = JwtHelper.DecodePayload(jwt);
        payload.GetProperty("iss").GetString().Should().EndWith("/realms/decisya");
        var exp = payload.GetProperty("exp").GetInt64();
        var iat = payload.GetProperty("iat").GetInt64();
        (exp - iat).Should().BeLessThanOrEqualTo(300);
    }

    private static List<string> ReadStringArray(JsonElement payload, string claim)
    {
        payload.TryGetProperty(claim, out var element).Should().BeTrue($"the access token should carry a '{claim}' claim");
        element.ValueKind.Should().Be(JsonValueKind.Array, $"'{claim}' should be a flat array");
        return element.EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    private static List<string> ReadAudiences(JsonElement payload)
    {
        if (!payload.TryGetProperty("aud", out var audElement))
        {
            return [];
        }

        return audElement.ValueKind switch
        {
            JsonValueKind.String => [audElement.GetString()!],
            JsonValueKind.Array => audElement.EnumerateArray().Select(e => e.GetString()!).ToList(),
            _ => [],
        };
    }
}
