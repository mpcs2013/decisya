using Microsoft.AspNetCore.Http.HttpResults;

namespace Decisya.Bff.Security;

/// <summary>
/// T-06 (G4-18-05): validates a caller-supplied <c>returnUrl</c> with the framework's own
/// local-URL check (<see cref="RedirectHttpResult.IsLocalUrl(string?)"/>), never a
/// hand-rolled prefix test — a hand-rolled check misses control characters a browser strips
/// (e.g. a decoded tab turns <c>/&lt;TAB&gt;/evil.test</c> into <c>//evil.test</c>). Applied
/// to both the login challenge and the already-signed-in shortcut (G2).
/// </summary>
internal static class ReturnUrlValidator
{
    internal const string Default = "/";

    internal static string Sanitize(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && RedirectHttpResult.IsLocalUrl(returnUrl)
            ? returnUrl
            : Default;
}
