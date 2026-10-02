using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Decisya.Api.Authentication;

/// <summary>
/// Gives every policy 403 the same generic <c>ProblemDetails</c> body as
/// <see cref="CallerContextMiddleware"/>'s 403: <c>type</c>, <c>title</c>, <c>status</c> and
/// <c>traceId</c>, with no reason (issue #25, G2 D5; G3 G4-25-03; closes #21 S-2). Only
/// <see cref="PolicyAuthorizationResult.Forbidden"/> is rewritten. Everything else, the 401
/// challenge included, goes to the framework's own handler, so a 401 stays the bare
/// <c>WWW-Authenticate: Bearer</c> of #20. Nothing is written once the response has started.
/// The policy name, the requirement, the failure reasons and every claim stay out of the body.
/// </summary>
internal sealed class ProblemDetailsAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        if (!authorizeResult.Forbidden)
        {
            await _default.HandleAsync(next, context, policy, authorizeResult).ConfigureAwait(false);
            return;
        }

        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;

        var problemDetailsService = context.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = { Status = StatusCodes.Status403Forbidden },
        }).ConfigureAwait(false);
    }
}
