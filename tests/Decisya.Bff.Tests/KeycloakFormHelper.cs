using System.Net;
using System.Text.RegularExpressions;

namespace Decisya.Bff.Tests;

/// <summary>
/// Extracts the <c>kc-form-login</c> form's <c>action</c> URL from a Keycloak login page, the
/// same way <c>Decisya.Identity.Tests/OidcTestHelpers.ExtractLoginFormAction</c> does (not
/// shared across projects: G2 does not link test helpers between the two projects).
/// </summary>
internal static class KeycloakFormHelper
{
    internal static string ExtractLoginFormAction(string html)
    {
        var formTag = Regex.Matches(html, "<form\\b[^>]*>", RegexOptions.IgnoreCase)
            .Select(match => match.Value)
            .FirstOrDefault(value => value.Contains("kc-form-login", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("No kc-form-login form was found on the Keycloak login page.");

        var actionMatch = Regex.Match(formTag, "action=\"([^\"]*)\"", RegexOptions.IgnoreCase);
        if (!actionMatch.Success)
        {
            throw new InvalidOperationException("The kc-form-login form has no action attribute.");
        }

        return WebUtility.HtmlDecode(actionMatch.Groups[1].Value);
    }
}
