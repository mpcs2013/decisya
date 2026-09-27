using Microsoft.AspNetCore.Antiforgery;

namespace Decisya.Bff.Security;

/// <summary>
/// D4: the double-submit antiforgery check as an explicit endpoint filter, not
/// <c>UseAntiforgery()</c> (which only enforces tokens on form-bound minimal-API endpoints,
/// and <c>/bff/logout</c> binds no form). Runs after authentication and authorization
/// (endpoint filters execute inside endpoint invocation, downstream of both middlewares),
/// which matters because the token pair is bound to the caller's <c>sub</c> (G3, T-04).
/// </summary>
internal sealed class AntiforgeryEndpointFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var httpContext = context.HttpContext;
        var skipsAntiforgery = httpContext.GetEndpoint()?.Metadata.GetMetadata<SkipAntiforgeryMetadata>() is not null;

        if (!skipsAntiforgery && IsMutatingMethod(httpContext.Request.Method))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(httpContext);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "The request could not be verified.");
            }
        }

        return await next(context);
    }

    private static bool IsMutatingMethod(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
}
