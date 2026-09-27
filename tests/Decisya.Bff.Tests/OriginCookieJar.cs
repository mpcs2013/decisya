namespace Decisya.Bff.Tests;

/// <summary>
/// A minimal, explicit per-origin cookie jar for driving the login flow with a plain
/// <see cref="HttpClient"/> ("browser-shaped, no browser", mirrors
/// <c>Decisya.Identity.Tests/SecureCookieRelayHandler</c>, generalised to two origins: the
/// in-process BFF and the Testcontainers Keycloak). Ignores every cookie attribute —
/// <c>HttpOnly</c>, <c>Secure</c>, <c>SameSite</c>, <c>Path</c> — on purpose: those are exactly
/// what the tests assert on the raw <c>Set-Cookie</c> header, not what should filter which
/// cookies this jar re-sends. Never referenced by production code.
/// </summary>
internal sealed class OriginCookieJar
{
    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Cookies => _cookies;

    public void CaptureFrom(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
        {
            return;
        }

        foreach (var header in setCookieHeaders)
        {
            var firstSegment = header.Split(';', 2)[0];
            var parts = firstSegment.Split('=', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            var name = parts[0].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            var isExpired = header.Contains("Max-Age=0", StringComparison.OrdinalIgnoreCase)
                || header.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase);

            if (isExpired)
            {
                _cookies.Remove(name);
            }
            else
            {
                _cookies[name] = parts[1].Trim();
            }
        }
    }

    public void ApplyTo(HttpRequestMessage request)
    {
        if (_cookies.Count == 0)
        {
            return;
        }

        request.Headers.TryAddWithoutValidation(
            "Cookie", string.Join("; ", _cookies.Select(pair => $"{pair.Key}={pair.Value}")));
    }
}
