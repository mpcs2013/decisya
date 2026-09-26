using System.Net;
using Decisya.AppHost;

namespace Decisya.Identity.Tests;

/// <summary>
/// G3 T-01 (fail-open) regression guard, added after a live #17 finding: the realm export
/// originally used Keycloak's <c>${env.VAR}</c> placeholder form, which Keycloak 26.7.4 does
/// not substitute in a partial realm import — the literal text was stored verbatim as the
/// credential value (confirmed live against Marco's AppHost: logging in as
/// <c>dev-alice</c> with the literal string <c>${env.DECISYA_DEV_USER_PASSWORD}</c> as the
/// password succeeded and issued a code). <c>deploy/keycloak/decisya-realm.json</c> now uses
/// Keycloak's other documented form, the unprefixed <c>${VAR}</c>, which this project's
/// Integration tests confirm Keycloak 26.7.4 does substitute for both the client secret
/// (<see cref="RealmConfigurationTests.The_stored_client_secret_equals_the_environment_value_the_placeholder_resolved_to"/>)
/// and a seeded user's password (<see cref="BffLoginFlowTests"/>'s positive-flow tests, once
/// <see cref="SecureCookieRelayHandler"/> stopped the Secure-cookie-over-http test-harness
/// artifact that was masking that success as a 400). This class proves the negative side:
/// neither the old, wrong <c>${env.VAR}</c> literal, the new, correct <c>${VAR}</c> literal,
/// nor an empty string ever authenticates a real user, and each produces Keycloak's own
/// invalid-credentials response (200 plus "Invalid username or password.") rather than
/// merely "not a successful redirect" — so a regression that made login fail for an
/// unrelated reason (for example the very cookie bug this class's own fixture-sharing note
/// warns about) could not slip past these assertions unnoticed.
/// </summary>
/// <remarks>
/// Uses its own container, never the shared <see cref="KeycloakRealmFixture"/> instance:
/// each case below is a deliberately wrong password, and G2 already rules out wrong-password
/// attempts against the shared fixture (they would bump dev-alice's brute-force counter and
/// could affect every other test sharing that instance).
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PlaceholderSubstitutionRegressionTests : IAsyncDisposable
{
    private const string ClientId = "decisya-bff";
    private const string RegisteredRedirectUri = "https://localhost:7200/signin-oidc";
    private const string InvalidCredentialsMessage = "Invalid username or password.";

    private Testcontainers.Keycloak.KeycloakContainer? _container;

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(nameof(RealmSecretRules.DevUserPasswordPlaceholderLiteral))]
    [InlineData(nameof(RealmSecretRules.LegacyEnvPrefixedDevUserPasswordPlaceholderLiteral))]
    public async Task A_login_attempt_using_a_literal_unresolved_placeholder_text_as_the_password_is_rejected_as_invalid_credentials(
        string literalName)
    {
        var literalPlaceholderText = literalName == nameof(RealmSecretRules.DevUserPasswordPlaceholderLiteral)
            ? RealmSecretRules.DevUserPasswordPlaceholderLiteral
            : RealmSecretRules.LegacyEnvPrefixedDevUserPasswordPlaceholderLiteral;

        await AssertPasswordIsRejectedAsInvalidCredentialsAsync(literalPlaceholderText);
    }

    [Fact]
    public async Task A_login_attempt_using_an_empty_password_is_rejected_as_invalid_credentials()
    {
        await AssertPasswordIsRejectedAsInvalidCredentialsAsync(string.Empty);
    }

    /// <summary>
    /// Starts a fresh container, attempts to log dev-alice in with <paramref name="password"/>,
    /// and asserts Keycloak's own invalid-credentials outcome: HTTP 200 (the login form
    /// re-rendered, not a redirect) with "Invalid username or password." in the body. Never
    /// asserts merely "not a 302": that would also be satisfied by an unrelated failure (for
    /// example a missing auth-session cookie, G4's own live finding), which would make this
    /// guard meaningless.
    /// </summary>
    private async Task AssertPasswordIsRejectedAsInvalidCredentialsAsync(string password)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var clientSecret = KeycloakRealmFixture.GenerateHex(32);
        var devUserPassword = KeycloakRealmFixture.GenerateHex(20);
        var adminPassword = KeycloakRealmFixture.GenerateHex(24);

        _container = KeycloakRealmFixture.BuildContainer(clientSecret, devUserPassword, adminPassword);
        await _container.StartAsync(cancellationToken);

        // SecureCookieRelayHandler, not CookieContainer (G4 finding): see its doc comment.
        using var httpClient = new HttpClient(new SecureCookieRelayHandler())
        {
            BaseAddress = new Uri(_container.GetBaseAddress()),
        };

        var (_, challenge) = OidcTestHelpers.GeneratePkce();
        var authorizeUrl =
            $"/realms/decisya/protocol/openid-connect/auth?client_id={ClientId}&response_type=code" +
            "&scope=openid&redirect_uri=" + Uri.EscapeDataString(RegisteredRedirectUri) +
            "&state=state&code_challenge=" + challenge + "&code_challenge_method=S256";

        using var authorizeResponse = await httpClient.GetAsync(authorizeUrl, cancellationToken);
        var html = await authorizeResponse.Content.ReadAsStringAsync(cancellationToken);
        var formAction = OidcTestHelpers.ExtractLoginFormAction(html);

        using var loginResponse = await httpClient.PostAsync(
            formAction,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "dev-alice",
                ["password"] = password,
            }),
            cancellationToken);

        var body = await loginResponse.Content.ReadAsStringAsync(cancellationToken);

        loginResponse.StatusCode.Should().Be(
            HttpStatusCode.OK, "a wrong password re-renders the login form, it does not redirect");
        body.Should().Contain(
            InvalidCredentialsMessage,
            "the login form should show Keycloak's own invalid-credentials message, not an unrelated error");
    }
}
