using Decisya.Bff.Security;

namespace Decisya.Bff.Proxy;

/// <summary>
/// B-3 (D4): the <c>/api</c> half of the shared antiforgery check. YARP endpoints never run
/// endpoint filters (the reason <see cref="AntiforgeryEndpointFilter"/> cannot cover this
/// route), so the check runs as a proxy-pipeline middleware instead, after
/// <c>UseAuthentication</c>/<c>UseAuthorization</c> (so the pair stays bound to the caller's
/// <c>sub</c>, T-04) and before <see cref="AccessTokenMiddleware"/> (a request rejected here
/// never even asks for a token). No <c>/api</c> route carries
/// <see cref="SkipAntiforgeryMetadata"/>.
/// </summary>
internal sealed class ApiAntiforgeryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!await AntiforgeryCheck.ValidateAsync(context).ConfigureAwait(false))
        {
            await Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "The request could not be verified.")
                .ExecuteAsync(context)
                .ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}
