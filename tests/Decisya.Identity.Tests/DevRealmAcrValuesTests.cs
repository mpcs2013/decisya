using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Decisya.Identity.Tests;

/// <summary>
/// Issue #121, G2 D3 and D7-1: the BFF now sends <c>acr_values=2</c> on every authorize request.
/// The dev realm has no level-of-authentication conditions, so a dev login must still complete and
/// Keycloak must answer with <c>acr</c> "1" (not fail, not prompt). The dev AppHost therefore sets
/// <c>Authentication:RequireAdminMfa=false</c> for the Api in run mode (platform-dev's file).
/// </summary>
[Trait("Category", "Integration")]
public class DevRealmAcrValuesTests : IClassFixture<DevRealmAcrValuesTests.OwnKeycloak>
{
    private const string ClientId = "decisya-bff";
    private const string RegisteredRedirectUri = "https://localhost:7200/signin-oidc";

    private readonly OwnKeycloak _fixture;

    /// <summary>
    /// Why this class has its own Keycloak and not the assembly-shared <see cref="KeycloakRealmFixture"/>:
    /// in the CI lane (every Integration class in one run) the shared instance answered 200 (the login form
    /// again) instead of 302 for dev-admin, while the class passed alone. The shared instance serves several
    /// classes in parallel under heavy container load and carries state from them; this test asserts "no OTP
    /// step, no prompt" and so needs a pristine realm and no concurrent users. One container per class
    /// (both cases), started lazily so the Docker-free unit step never touches Docker.
    /// </summary>
    public DevRealmAcrValuesTests(OwnKeycloak fixture)
    {
        _fixture = fixture;
    }

    public sealed class OwnKeycloak : IAsyncLifetime
    {
        private readonly SemaphoreSlim _startLock = new(1, 1);
        private Testcontainers.Keycloak.KeycloakContainer? _container;

        public string ClientSecret { get; private set; } = string.Empty;

        public string DevUserPassword { get; private set; } = string.Empty;

        public string BaseAddress =>
            _container?.GetBaseAddress() ?? throw new InvalidOperationException("Call EnsureStartedAsync first.");

        public ValueTask InitializeAsync() => ValueTask.CompletedTask;

        public async Task EnsureStartedAsync(CancellationToken cancellationToken)
        {
            await _startLock.WaitAsync(cancellationToken);
            try
            {
                if (_container is not null)
                {
                    return;
                }

                ClientSecret = KeycloakRealmFixture.GenerateHex(32);
                DevUserPassword = KeycloakRealmFixture.GenerateHex(20);
                var container = KeycloakRealmFixture.BuildContainer(
                    ClientSecret, DevUserPassword, KeycloakRealmFixture.GenerateHex(24));
                await container.StartAsync(cancellationToken);
                _container = container;
            }
            finally
            {
                _startLock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_container is not null)
            {
                await _container.DisposeAsync();
            }

            _startLock.Dispose();
        }
    }

    /// <summary>The page's own error text (feedback or input error), never the whole page or any credential.</summary>
    private static string DescribePage(string html)
    {
        var texts = System.Text.RegularExpressions.Regex
            .Matches(html, "id=\"(?:input-error|kc-error-message|kc-feedback)[^\"]*\"[^>]*>(.*?)</", System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select(m => System.Text.RegularExpressions.Regex.Replace(m.Groups[1].Value, "<[^>]+>|\\s+", " ").Trim())
            .Where(t => t.Length > 0)
            .ToList();
        var action = System.Text.RegularExpressions.Regex.Match(html, "<form\\b[^>]*action=\"([^\"]*)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var title = System.Text.RegularExpressions.Regex.Match(html, "<title>(.*?)</title>", System.Text.RegularExpressions.RegexOptions.Singleline);
        // The form action carries session_code/tab_id but no credential; strip the query string anyway.
        var actionPath = action.Success ? action.Groups[1].Value.Split('?')[0] : "(no form)";
        // Braces are doubled: the text becomes part of a format string in the assertion message.
        return $"page title='{title.Groups[1].Value.Trim()}'; form action path='{actionPath}'; error text='{string.Join(" | ", texts)}'"
            .Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dev-alice")]
    [InlineData("dev-admin")]
    public async Task A_dev_login_with_acr_values_2_still_completes_and_keycloak_answers_acr_1(string username)
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.EnsureStartedAsync(ct);
        using var client = new HttpClient(new SecureCookieRelayHandler()) { BaseAddress = new Uri(_fixture.BaseAddress) };

        var (verifier, challenge) = OidcTestHelpers.GeneratePkce();
        using var authorize = await client.GetAsync(
            "/realms/decisya/protocol/openid-connect/auth"
            + $"?client_id={ClientId}&response_type=code&scope=openid"
            + $"&redirect_uri={Uri.EscapeDataString(RegisteredRedirectUri)}"
            + $"&state={Guid.NewGuid():N}&nonce={Guid.NewGuid():N}"
            + $"&code_challenge={challenge}&code_challenge_method=S256&acr_values=2",
            ct);
        authorize.StatusCode.Should().Be(HttpStatusCode.OK, "an unknown acr_values must not break the dev login page");
        var action = OidcTestHelpers.ExtractLoginFormAction(await authorize.Content.ReadAsStringAsync(ct));

        using var login = await client.PostAsync(
            action,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = username, ["password"] = _fixture.DevUserPassword }),
            ct);
        var loginBody = login.StatusCode == HttpStatusCode.Found ? string.Empty : await login.Content.ReadAsStringAsync(ct);
        login.StatusCode.Should().Be(
            HttpStatusCode.Found,
            "the dev login must complete with no OTP step"
            + (login.StatusCode == HttpStatusCode.Found ? string.Empty : $" ({username}: {DescribePage(loginBody)})"));
        var query = OidcTestHelpers.ParseQuery(login.Headers.Location!.Query);
        query.Should().ContainKey("code");

        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "/realms/decisya/protocol/openid-connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = query["code"],
                ["redirect_uri"] = RegisteredRedirectUri,
                ["code_verifier"] = verifier,
            }),
        };
        tokenRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{ClientId}:{_fixture.ClientSecret}")));
        using var token = await client.SendAsync(tokenRequest, ct);
        token.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await token.Content.ReadAsStringAsync(ct));
        var payload = JwtHelper.DecodePayload(document.RootElement.GetProperty("access_token").GetString()!);
        payload.GetProperty("acr").ValueKind.Should().Be(JsonValueKind.String);
        payload.GetProperty("acr").GetString().Should().Be("1");
    }
}
