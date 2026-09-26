namespace Decisya.Identity.Tests;

/// <summary>
/// G4 root-cause finding (issue #17): Keycloak 26.7.4 issues its auth-session cookies
/// (<c>AUTH_SESSION_ID</c>, <c>KC_RESTART</c>, <c>KC_AUTH_SESSION_HASH</c>) with the
/// <c>Secure</c> attribute even on a plain <c>http://</c> origin. <see cref="System.Net.CookieContainer"/>
/// correctly implements RFC 6265 and never re-sends a <c>Secure</c> cookie on a
/// non-<c>https</c> request; every Testcontainers-mapped Keycloak endpoint in this project
/// is <c>http://localhost:&lt;port&gt;</c>, so <c>CookieContainer</c> silently dropped these
/// cookies on the login form's own POST, which Keycloak then rejected with 400 ("Restart
/// login cookie not found") — a test-harness artifact, confirmed by capturing that exact
/// response body, not a Keycloak realm-import or placeholder-substitution defect. A real
/// browser (Marco's manual check) carries them over TLS; #18's BFF will too, against
/// whatever scheme Keycloak's real deployment uses.
/// </summary>
/// <remarks>
/// This relay is Integration-test-only. It stands in for the TLS layer this throwaway,
/// loopback-only container deliberately doesn't have, by keeping the exact <c>Set-Cookie</c>
/// name/value pairs Keycloak returned and re-attaching them verbatim on every later request
/// to that same container, ignoring the <c>Secure</c> attribute. It parses only the
/// <c>name=value</c> pair (never any attribute), and it is never referenced by any
/// production or BFF code path.
/// </remarks>
internal sealed class SecureCookieRelayHandler : DelegatingHandler
{
    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

    public SecureCookieRelayHandler()
        : base(new HttpClientHandler { AllowAutoRedirect = false })
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_cookies.Count > 0)
        {
            request.Headers.TryAddWithoutValidation(
                "Cookie", string.Join("; ", _cookies.Select(pair => $"{pair.Key}={pair.Value}")));
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
        {
            foreach (var setCookieHeader in setCookieHeaders)
            {
                var nameValue = setCookieHeader.Split(';', 2)[0];
                var parts = nameValue.Split('=', 2);
                if (parts.Length == 2 && parts[0].Trim().Length > 0)
                {
                    _cookies[parts[0].Trim()] = parts[1].Trim();
                }
            }
        }

        return response;
    }
}
