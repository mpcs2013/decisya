using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Yarp.ReverseProxy.Transforms;

namespace Decisya.Bff.Proxy;

/// <summary>
/// G3's new MUST (T-05): every upstream 5xx response reaches the browser with its body
/// replaced by the BFF's own generic ProblemDetails, status code kept. Checked on
/// <c>HttpContext.Response.StatusCode</c> rather than <c>context.ProxyResponse</c>, because a
/// request-creation failure (<see cref="AttachAccessTokenRequestTransform"/>'s https-only
/// refusal, T-02) leaves <c>ProxyResponse</c> <see langword="null"/> with YARP's own 502
/// already set — that case must be covered too, and empirically it still runs this transform.
/// A 4xx body (a future #20 ProblemDetails) and any header (e.g. <c>WWW-Authenticate</c>) still
/// pass through unchanged.
/// </summary>
internal sealed class ReplaceUpstreamErrorBodyResponseTransform : ResponseTransform
{
    public override async ValueTask ApplyAsync(ResponseTransformContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var statusCode = context.HttpContext.Response.StatusCode;
        if (statusCode < StatusCodes.Status500InternalServerError)
        {
            return;
        }

        context.SuppressResponseBody = true;

        // The headers already copied from the upstream response describe its (now discarded)
        // body; both must go before a new, differently-sized body is written, or the response
        // is malformed.
        context.HttpContext.Response.Headers.Remove(HeaderNames.ContentLength);
        context.HttpContext.Response.Headers.Remove(HeaderNames.ContentEncoding);

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = "The upstream service returned an unexpected error.",
        };

        // The contentType argument must be explicit: WriteAsJsonAsync's own default
        // ("application/json") would otherwise overwrite ContentType regardless of what is
        // set beforehand.
        await context.HttpContext.Response.WriteAsJsonAsync(
            problem, options: null, contentType: "application/problem+json", context.CancellationToken).ConfigureAwait(false);
    }
}
