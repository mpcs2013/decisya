using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Decisya.Bff.Security;

/// <summary>
/// #26 G2 D6 and G3 S-b: the security headers on every response the BFF writes (the shell, the
/// assets, <c>/bff/*</c>, proxied <c>/api</c> responses, redirects, 401s and the exception
/// handler's 500). Registered through <c>Response.OnStarting</c> as the first middleware, so the
/// values survive <c>UseExceptionHandler</c> clearing the headers.
/// </summary>
/// <remarks>
/// The CSP, X-Frame-Options, nosniff, Referrer-Policy, COOP, CORP and Permissions-Policy are always
/// overwritten: a proxied upstream header can never weaken the BFF's policy (G3 S-b, H-01).
/// Only <c>Cache-Control</c> is set when absent, because a more specific value (immutable assets,
/// <c>no-cache</c> on <c>index.html</c>, the Api's own <c>no-store</c>) must win.
/// No <c>unsafe-inline</c>, <c>unsafe-eval</c>, nonce or hash: the SPA has no inline script or style.
/// </remarks>
internal static class SecurityHeaders
{
    internal const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; "
        + "connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; "
        + "frame-ancestors 'none'; frame-src 'none'; worker-src 'none'";

    internal const string PermissionsPolicy =
        "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";

    internal const string DefaultCacheControl = "no-store";

    /// <summary>Adds the middleware. Call it before every other middleware.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(static (context, next) =>
        {
            context.Response.OnStarting(static state =>
            {
                Apply(((HttpResponse)state).Headers);
                return Task.CompletedTask;
            }, context.Response);

            return next(context);
        });
    }

    internal static void Apply(IHeaderDictionary headers)
    {
        headers[HeaderNames.ContentSecurityPolicy] = ContentSecurityPolicy;
        headers[HeaderNames.XContentTypeOptions] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers[HeaderNames.XFrameOptions] = "DENY";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["Permissions-Policy"] = PermissionsPolicy;

        if (StringValues.IsNullOrEmpty(headers.CacheControl))
        {
            headers.CacheControl = DefaultCacheControl;
        }
    }
}
