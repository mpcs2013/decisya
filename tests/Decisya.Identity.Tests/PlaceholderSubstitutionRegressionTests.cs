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
    /// G6-03: pins Keycloak 26.7.4's own observed behaviour when a <c>${VAR}</c>
    /// placeholder's variable is never set at import time at all (not even to an empty
    /// string) — the literal placeholder text is kept as the credential value, exactly the
    /// same T-01 fail-open already known for the old <c>${env.VAR}</c> form. Confirmed live
    /// (manual <c>docker run</c>, no CI, no secret printed): a login with the literal string
    /// <c>${DECISYA_DEV_USER_PASSWORD}</c> as the password succeeds. This is not a defect to
    /// fix here: every launch path that exists today (the AppHost guard, the generated
    /// parameters, and <see cref="KeycloakRealmFixture"/>'s own CI-fail branch) always
    /// supplies a value, so the precondition — an unset variable — never occurs. #29's
    /// Compose path must use Keycloak's <c>${VAR:?}</c> form so an unset variable fails the
    /// deployment instead of falling open like this.
    /// </summary>
    [Fact]
    public async Task An_unset_dev_user_password_variable_leaves_the_literal_placeholder_as_the_credential_value()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var clientSecret = KeycloakRealmFixture.GenerateHex(32);
        var adminPassword = KeycloakRealmFixture.GenerateHex(24);

        // Deliberately not KeycloakRealmFixture.BuildContainer, which always sets both
        // placeholder variables: this builds the same image/realm wiring directly, with
        // DECISYA_DEV_USER_PASSWORD never set on the container at all.
        var image = ContainerImages.Reference(
            ContainerImages.KeycloakRegistry, ContainerImages.KeycloakImage,
            ContainerImages.KeycloakTag, ContainerImages.KeycloakSha256);
        var realmFilePath = RepoPaths.Find(Path.Combine("deploy", "keycloak", "decisya-realm.json"));

        _container = new Testcontainers.Keycloak.KeycloakBuilder(image)
            .WithUsername("admin")
            .WithPassword(adminPassword)
            .WithRealm(realmFilePath)
            .WithEnvironment("DECISYA_BFF_CLIENT_SECRET", clientSecret)
            .Build();
        await _container.StartAsync(cancellationToken);

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
        var formAction = OidcTestHelpers.ExtractLoginFormAction(await authorizeResponse.Content.ReadAsStringAsync(cancellationToken));

        using var loginResponse = await httpClient.PostAsync(
            formAction,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "dev-alice",
                ["password"] = RealmSecretRules.DevUserPasswordPlaceholderLiteral,
            }),
            cancellationToken);

        loginResponse.StatusCode.Should().Be(
            HttpStatusCode.Found,
            "observed behaviour (G6-03): Keycloak 26.7.4 keeps the literal placeholder text " +
            "as the credential value when its variable is never set at import time");
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
