namespace Decisya.Api.Authentication;

/// <summary>
/// Sets <c>Cache-Control: no-store</c> on every response (issue #25, G2 D5). The API has no
/// cacheable response: every body is per caller. Runs first in the pipeline, and sets the header
/// in <c>OnStarting</c>, so the 401 challenge, every 403, the endpoint results and the exception
/// handler's rebuilt 500 all carry it, however earlier code touched the headers.
/// </summary>
public static class NoStoreResponses
{
    /// <summary>Adds the middleware. Call it before every other middleware.</summary>
    public static IApplicationBuilder UseNoStoreResponses(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(static (context, next) =>
        {
            context.Response.OnStarting(static state =>
            {
                ((HttpResponse)state).Headers.CacheControl = "no-store";
                return Task.CompletedTask;
            }, context.Response);

            return next(context);
        });
    }
}
