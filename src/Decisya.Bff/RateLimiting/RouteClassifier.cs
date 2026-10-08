using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Decisya.Bff.RateLimiting;

/// <summary>
/// Decides a request's route class (G2 D2). Endpoint metadata first; then the three OIDC handler
/// paths, which have no endpoint (the handler serves them inside <c>UseAuthentication</c>) and are
/// compared the way the handler compares them, <see cref="PathString"/> equality, case-insensitive
/// (G3 G4-122-01 d); then a fail-closed fallback so an unmapped <c>/api</c> or <c>/bff</c> request is
/// never unlimited. An <c>api</c> request under <c>/api/admin</c> is upgraded to <c>admin</c>. A wrong
/// class changes only which bucket counts, never who may do what: the Api still authorizes.
/// </summary>
internal static class RouteClassifier
{
    private const int MaxDecodePasses = 3;

    internal static RouteClass? Classify(HttpContext context, OpenIdConnectOptions oidc)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(oidc);

        var path = context.Request.Path;
        RouteClass? routeClass = context.GetEndpoint()?.Metadata.GetMetadata<RouteClassMetadata>()?.Class;

        if (routeClass is null && IsOidcPath(path, oidc))
        {
            routeClass = RouteClass.Login;
        }

        if (routeClass is null
            && (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                || path.StartsWithSegments("/bff", StringComparison.OrdinalIgnoreCase)))
        {
            routeClass = RouteClass.Api;
        }

        return routeClass == RouteClass.Api && IsAdminPath(path) ? RouteClass.Admin : routeClass;
    }

    internal static bool IsOidcPath(PathString path, OpenIdConnectOptions oidc)
    {
        ArgumentNullException.ThrowIfNull(oidc);

        return Matches(path, oidc.CallbackPath)
            || Matches(path, oidc.SignedOutCallbackPath)
            || Matches(path, oidc.RemoteSignOutPath);
    }

    /// <summary>
    /// True when the path is <c>/api/admin</c> or below. Doubtful forms round up to admin: case variants,
    /// repeated slashes, percent-encoded letters (decoded up to three times), backslashes and dot
    /// segments (the literal and the resolved reading are both checked).
    /// </summary>
    internal static bool IsAdminPath(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        for (var pass = 0; pass <= MaxDecodePasses; pass++)
        {
            if (HasAdminSegments(value, resolveDots: false) || HasAdminSegments(value, resolveDots: true))
            {
                return true;
            }

            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(value);
            }
            catch (UriFormatException)
            {
                return false;
            }

            if (string.Equals(decoded, value, StringComparison.Ordinal))
            {
                return false;
            }

            value = decoded;
        }

        return false;
    }

    private static bool Matches(PathString path, PathString configured) =>
        configured.HasValue && path.Equals(configured, StringComparison.OrdinalIgnoreCase);

    private static bool HasAdminSegments(string value, bool resolveDots)
    {
        var segments = new List<string>();
        foreach (var raw in value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = raw.Trim();
            if (resolveDots && segment == ".")
            {
                continue;
            }

            if (resolveDots && segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        return segments.Count >= 2
            && string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[1], "admin", StringComparison.OrdinalIgnoreCase);
    }
}
