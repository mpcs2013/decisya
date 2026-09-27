namespace Decisya.Bff.Tests;

/// <summary>One <c>Set-Cookie</c> header's attributes, parsed for G4-18-01's cookie-flag
/// assertions. Test-only: production code never inspects a cookie's own attributes.</summary>
internal sealed record ParsedSetCookie(
    string Name,
    string Value,
    bool HttpOnly,
    bool Secure,
    string? SameSite,
    string? Path,
    string? Domain,
    bool HasExpiry);

internal static class SetCookieParser
{
    internal static ParsedSetCookie Parse(string header)
    {
        var segments = header.Split(';').Select(segment => segment.Trim()).ToList();
        var nameValue = segments[0].Split('=', 2);
        var name = nameValue[0];
        var value = nameValue.Length > 1 ? nameValue[1] : string.Empty;

        var httpOnly = false;
        var secure = false;
        string? sameSite = null;
        string? path = null;
        string? domain = null;
        var hasExpiry = false;

        foreach (var attribute in segments.Skip(1))
        {
            var parts = attribute.Split('=', 2);
            var attributeName = parts[0].Trim();
            var attributeValue = parts.Length > 1 ? parts[1].Trim() : null;

            if (string.Equals(attributeName, "HttpOnly", StringComparison.OrdinalIgnoreCase))
            {
                httpOnly = true;
            }
            else if (string.Equals(attributeName, "Secure", StringComparison.OrdinalIgnoreCase))
            {
                secure = true;
            }
            else if (string.Equals(attributeName, "SameSite", StringComparison.OrdinalIgnoreCase))
            {
                sameSite = attributeValue;
            }
            else if (string.Equals(attributeName, "Path", StringComparison.OrdinalIgnoreCase))
            {
                path = attributeValue;
            }
            else if (string.Equals(attributeName, "Domain", StringComparison.OrdinalIgnoreCase))
            {
                domain = attributeValue;
            }
            else if (string.Equals(attributeName, "Expires", StringComparison.OrdinalIgnoreCase)
                || string.Equals(attributeName, "Max-Age", StringComparison.OrdinalIgnoreCase))
            {
                hasExpiry = true;
            }
        }

        return new ParsedSetCookie(name, value, httpOnly, secure, sameSite, path, domain, hasExpiry);
    }

    internal static IEnumerable<ParsedSetCookie> ParseAll(IReadOnlyDictionary<string, string[]> headers)
    {
        if (!headers.TryGetValue("Set-Cookie", out var values))
        {
            return [];
        }

        return values.Select(Parse);
    }

    internal static IEnumerable<ParsedSetCookie> ParseAll(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return [];
        }

        return values.Select(Parse);
    }
}
