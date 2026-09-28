namespace Decisya.Bff.Security;

/// <summary>
/// D4: the double-submit antiforgery check as an explicit endpoint filter, not
/// <c>UseAntiforgery()</c> (which only enforces tokens on form-bound minimal-API endpoints,
/// and <c>/bff/logout</c> binds no form). Runs after authentication and authorization
/// (endpoint filters execute inside endpoint invocation, downstream of both middlewares),
/// which matters because the token pair is bound to the caller's <c>sub</c> (G3, T-04). The
/// validation body itself lives in <see cref="AntiforgeryCheck"/>, shared with <c>/api</c>'s
/// own <c>ApiAntiforgeryMiddleware</c> (#19, D4).
/// </summary>
internal sealed class AntiforgeryEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var httpContext = context.HttpContext;
        var skipsAntiforgery = httpContext.GetEndpoint()?.Metadata.GetMetadata<SkipAntiforgeryMetadata>() is not null;

        if (!skipsAntiforgery && !await AntiforgeryCheck.ValidateAsync(httpContext).ConfigureAwait(false))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "The request could not be verified.");
        }

        return await next(context);
    }
}
