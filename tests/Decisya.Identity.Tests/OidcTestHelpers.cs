using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Decisya.Identity.Tests;

/// <summary>
/// Small, package-free helpers shared by every test that drives a real authorization-code +
/// PKCE flow against a Keycloak container with a plain <see cref="HttpClient"/>
/// ("browser-shaped, no browser", G2): PKCE generation, the login form's <c>action</c>
/// attribute, and query-string parsing on the final redirect.
/// </summary>
internal static class OidcTestHelpers
{
    public static (string Verifier, string Challenge) GeneratePkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    /// <summary>Extracts the <c>kc-form-login</c> form's <c>action</c> URL from a Keycloak
    /// login page, HTML-decoding <c>&amp;amp;</c> with the framework's own
    /// <see cref="WebUtility"/> (no HTML-parser package).</summary>
    public static string ExtractLoginFormAction(string html)
    {
        var formTag = Regex.Matches(html, "<form\\b[^>]*>", RegexOptions.IgnoreCase)
            .Select(m => m.Value)
            .FirstOrDefault(value => value.Contains("kc-form-login", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("No kc-form-login form was found on the Keycloak login page.");

        var actionMatch = Regex.Match(formTag, "action=\"([^\"]*)\"", RegexOptions.IgnoreCase);
        if (!actionMatch.Success)
        {
            throw new InvalidOperationException("The kc-form-login form has no action attribute.");
        }

        return WebUtility.HtmlDecode(actionMatch.Groups[1].Value);
    }

    public static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var trimmed = query.TrimStart('?');
        if (trimmed.Length == 0)
        {
            return result;
        }

        foreach (var pair in trimmed.Split('&'))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            result[key] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        }

        return result;
    }
}
