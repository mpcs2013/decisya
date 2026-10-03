using Decisya.Bff.Session;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace Decisya.Bff.Spa;

/// <summary>
/// #26 G2 D5: serves the built SPA (Vite writes into the web root) from the BFF's own origin.
/// <c>/assets/*</c> is static files, before the session guard and authentication, so an asset
/// request never touches the cookie handler or Redis. Every other GET/HEAD that no endpoint
/// claimed gets the constant <c>index.html</c> through one fallback endpoint, except the reserved
/// server prefixes, which stay 404. No code builds a file path from request data.
/// </summary>
internal static class SpaHosting
{
    internal const string AssetsSegment = "/assets";
    internal const string IndexFileName = "index.html";

    /// <summary>
    /// The only thing between an unknown server path and the shell document. A missing prefix would
    /// serve the static, identical, cookie-free shell (never data). Pinned by a test.
    /// </summary>
    internal static readonly PathString[] ReservedPrefixes =
    [
        new("/bff"),
        new("/api"),
        new("/signin-oidc"),
        new("/signout-callback-oidc"),
        new("/health"),
        new("/alive"),
        new(AssetsSegment),
    ];

    internal const string ImmutableCacheControl = "public, max-age=31536000, immutable";

    /// <summary>
    /// Serves <c>wwwroot/assets/**</c> only, with immutable caching (Vite file names are content
    /// hashed). <c>PhysicalFileProvider</c> refuses any path that resolves outside its root and
    /// excludes hidden, system and dot files; <c>ServeUnknownFileTypes</c> stays false; there is no
    /// directory browsing and no default files. Place it before <c>SessionFixationGuard</c>.
    /// </summary>
    internal static IApplicationBuilder UseSpaStaticAssets(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = new StaticFileOptions
        {
            FileProvider = WebRoot(app.Environment),
            ServeUnknownFileTypes = false,
            OnPrepareResponse = static context =>
                context.Context.Response.Headers[HeaderNames.CacheControl] = ImmutableCacheControl,
        };

        return app.UseWhen(
            static context => context.Request.Path.StartsWithSegments(AssetsSegment, StringComparison.OrdinalIgnoreCase),
            branch => branch.UseStaticFiles(options));
    }

    /// <summary>
    /// Maps the SPA fallback. Call it last. GET and HEAD only; the reserved prefixes and a missing
    /// build answer 404 with an empty body (no path, no stack trace). The framework's default
    /// pattern <c>{*path:nonfile}</c> never matches a last segment with a dot. No authorization
    /// metadata: the document is static and identical for every caller, and sets no cookie.
    /// </summary>
    internal static IEndpointConventionBuilder MapSpaFallback(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var webRoot = WebRoot(app.Environment);

        return app.MapFallback(async context =>
        {
            var isGet = HttpMethods.IsGet(context.Request.Method);
            if (!isGet && !HttpMethods.IsHead(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (IsReserved(context.Request.Path))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var index = webRoot.GetFileInfo(IndexFileName);
            if (!index.Exists || index.IsDirectory)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers[HeaderNames.CacheControl] = "no-cache";
            if (index.Length >= 0)
            {
                context.Response.ContentLength = index.Length;
            }

            if (!isGet)
            {
                return;
            }

            await using var stream = index.CreateReadStream();
            await stream.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Logs <see cref="BffLog.SpaIndexMissing"/> once when the web root holds no <c>index.html</c>
    /// (fixed text, no path). A web root that did not exist at start-up stays empty until restart.
    /// </summary>
    internal static void WarnIfIndexMissing(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var index = WebRoot(app.Environment).GetFileInfo(IndexFileName);
        if (!index.Exists)
        {
            BffLog.SpaIndexMissing(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Decisya.Bff.Spa"));
        }
    }

    /// <summary>
    /// The provider over the web root the host was configured with. Built from
    /// <c>IWebHostEnvironment.WebRootPath</c> (the effective value), never from
    /// <c>WebRootFileProvider</c>: that provider is created from the default <c>wwwroot</c> folder
    /// before a configured web root (<c>UseSetting(WebHostDefaults.WebRootKey, ...)</c>, as the tests
    /// do) is applied, so it can point somewhere else. A web root that does not exist at start-up
    /// gives an empty provider (restart after the first SPA build). <c>PhysicalFileProvider</c> keeps
    /// its default exclusion of hidden and system files and refuses paths outside its root.
    /// </summary>
    private static IFileProvider WebRoot(IWebHostEnvironment environment) =>
        !string.IsNullOrEmpty(environment.WebRootPath) && Directory.Exists(environment.WebRootPath)
            ? new PhysicalFileProvider(environment.WebRootPath)
            : new NullFileProvider();

    private static bool IsReserved(PathString path)
    {
        foreach (var prefix in ReservedPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
