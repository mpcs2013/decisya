using System.Net;

namespace Decisya.Bff.Tests;

/// <summary>One captured hop of the login flow, headers and body flattened so the response
/// itself can be disposed immediately (G2's token-leak scan reads these, not live
/// <see cref="HttpResponseMessage"/> instances).</summary>
internal sealed record CapturedExchange(
    string Step,
    HttpStatusCode StatusCode,
    IReadOnlyDictionary<string, string[]> Headers,
    string Body);

/// <summary>The outcome of <see cref="LoginFlowHarness.LogInAsync"/>: an authenticated BFF
/// client plus every hop the flow went through, for the token-leak scan and cookie-flag
/// assertions.</summary>
internal sealed record LoginFlowResult(
    HttpClient BffClient,
    OriginCookieJar BffJar,
    IReadOnlyList<CapturedExchange> Exchanges,
    Uri? FinalRedirectLocation);

/// <summary>
/// Drives the real authorization-code + PKCE flow with a plain <see cref="HttpClient"/>
/// against the in-process BFF and a real Testcontainers Keycloak (G2's test harness), the
/// same "browser-shaped, no browser" style <c>Decisya.Identity.Tests/BffLoginFlowTests</c>
/// uses for the OIDC client itself. Never auto-follows a redirect: each hop is inspected
/// explicitly, which is also how the flow correctly crosses from the in-process BFF to the
/// real Keycloak container and back.
/// </summary>
internal static class LoginFlowHarness
{
    /// <summary>An in-process client for <c>https://localhost:7200</c> with no cookie jar
    /// attached, for tests that need to control the <c>Cookie</c> header themselves.</summary>
    internal static HttpClient CreateRawBffClient(BffWebApplicationFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return new HttpClient(factory.Server.CreateHandler())
        {
            BaseAddress = new Uri("https://localhost:7200"),
        };
    }

    internal static Task<LoginFlowResult> LogInAsync(
        BffWebApplicationFactory factory,
        KeycloakBffFixture keycloakFixture,
        string username,
        string password,
        string returnUrl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var bffJar = new OriginCookieJar();
        var bffClient = new HttpClient(new OriginCookieHandler(bffJar, factory.Server.CreateHandler()))
        {
            BaseAddress = new Uri("https://localhost:7200"),
        };

        return LogInWithClientAsync(bffClient, bffJar, keycloakFixture, username, password, returnUrl, cancellationToken);
    }

    /// <summary>Same flow as <see cref="LogInAsync"/>, against a caller-supplied client and
    /// jar — for tests that need a cookie (e.g. an anonymously-issued antiforgery pair)
    /// captured before login to survive, unreplaced, into the authenticated session.</summary>
    internal static async Task<LoginFlowResult> LogInWithClientAsync(
        HttpClient bffClient,
        OriginCookieJar bffJar,
        KeycloakBffFixture keycloakFixture,
        string username,
        string password,
        string returnUrl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bffClient);
        ArgumentNullException.ThrowIfNull(bffJar);
        ArgumentNullException.ThrowIfNull(keycloakFixture);

        var exchanges = new List<CapturedExchange>();

        var keycloakJar = new OriginCookieJar();
        using var keycloakClient = new HttpClient(
            new OriginCookieHandler(keycloakJar, new HttpClientHandler { AllowAutoRedirect = false }))
        {
            BaseAddress = new Uri(keycloakFixture.BaseAddress),
        };

        using var challengeResponse = await bffClient.GetAsync(
            $"/bff/login?returnUrl={Uri.EscapeDataString(returnUrl)}", cancellationToken).ConfigureAwait(false);
        exchanges.Add(await CaptureAsync("bff-login-challenge", challengeResponse, cancellationToken).ConfigureAwait(false));
        challengeResponse.StatusCode.Should().Be(HttpStatusCode.Found, "an anonymous /bff/login should challenge");
        var authorizeUrl = challengeResponse.Headers.Location
            ?? throw new InvalidOperationException("The login challenge carried no Location header.");

        var formAction = await FollowToLoginFormAsync(keycloakClient, authorizeUrl, exchanges, cancellationToken).ConfigureAwait(false);

        using var loginFormResponse = await keycloakClient.PostAsync(
            formAction,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = username,
                ["password"] = password,
            }),
            cancellationToken).ConfigureAwait(false);
        var loginFormExchange = await CaptureAsync("keycloak-login-form", loginFormResponse, cancellationToken).ConfigureAwait(false);
        exchanges.Add(loginFormExchange);
        loginFormExchange.StatusCode.Should().Be(HttpStatusCode.Found, $"{username}'s login should redirect with a code");
        var callbackUrl = loginFormResponse.Headers.Location
            ?? throw new InvalidOperationException($"{username}'s login form response carried no Location header.");

        using var callbackResponse = await bffClient.GetAsync(callbackUrl, cancellationToken).ConfigureAwait(false);
        exchanges.Add(await CaptureAsync("bff-signin-oidc-callback", callbackResponse, cancellationToken).ConfigureAwait(false));

        return new LoginFlowResult(bffClient, bffJar, exchanges, callbackResponse.Headers.Location);
    }

    /// <summary>
    /// Follows Keycloak's own redirects from an <c>/auth</c> URL to the actual login page —
    /// the OIDC handler's <c>PushedAuthorizationBehavior</c> can make that a PAR call
    /// (<c>/auth?request_uri=...</c>), which itself 302s once more to
    /// <c>/login-actions/authenticate</c> before the login form appears — exactly like a
    /// browser would, without assuming either shape. Also used directly by
    /// <c>SessionFixationTests</c>, which drives this hop itself.
    /// </summary>
    internal static async Task<string> FollowToLoginFormAsync(
        HttpClient keycloakClient, Uri authorizeUrl, List<CapturedExchange> exchanges, CancellationToken cancellationToken)
    {
        Uri nextUrl = authorizeUrl;
        var redirectsFollowed = 0;
        while (true)
        {
            using var hopResponse = await keycloakClient.GetAsync(nextUrl, cancellationToken).ConfigureAwait(false);
            var exchange = await CaptureAsync("keycloak-authorize", hopResponse, cancellationToken).ConfigureAwait(false);
            exchanges.Add(exchange);

            if (exchange.StatusCode != HttpStatusCode.Found)
            {
                exchange.StatusCode.Should().Be(HttpStatusCode.OK, "the Keycloak login page should render");
                return KeycloakFormHelper.ExtractLoginFormAction(exchange.Body);
            }

            redirectsFollowed++;
            if (redirectsFollowed > 3)
            {
                throw new InvalidOperationException("Too many redirects following the Keycloak authorize request.");
            }

            nextUrl = hopResponse.Headers.Location
                ?? throw new InvalidOperationException("A Keycloak authorize redirect carried no Location header.");
        }
    }

    /// <summary>Reads and flattens one response so it can be inspected after disposal (also
    /// used by tests that add their own hops after <see cref="LogInAsync"/> returns, e.g.
    /// <c>/bff/me</c> and <c>/bff/logout</c>).</summary>
    internal static async Task<CapturedExchange> CaptureAsync(
        string step, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
            headers[header.Key] = header.Value.ToArray();
        }

        foreach (var header in response.Content.Headers)
        {
            headers[header.Key] = header.Value.ToArray();
        }

        return new CapturedExchange(step, response.StatusCode, headers, body);
    }
}
