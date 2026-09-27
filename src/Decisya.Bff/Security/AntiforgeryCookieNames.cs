namespace Decisya.Bff.Security;

/// <summary>D4's three antiforgery names, shared by <c>Program.cs</c>'s <c>AddAntiforgery</c>
/// configuration and the <c>/bff/me</c> handler that issues the JS-readable cookie.</summary>
internal static class AntiforgeryCookieNames
{
    /// <summary>The <c>HttpOnly</c> secret half (the "cookie token"), read automatically by
    /// <see cref="Microsoft.AspNetCore.Antiforgery.IAntiforgery"/> itself.</summary>
    internal const string CookieToken = "__Host-decisya-af";

    /// <summary>The JS-readable copy of the request token, echoed by the SPA in
    /// <see cref="HeaderName"/>.</summary>
    internal const string XsrfCookie = "__Host-decisya-xsrf";

    internal const string HeaderName = "X-XSRF-TOKEN";
}
