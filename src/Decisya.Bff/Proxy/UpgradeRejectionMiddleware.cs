using Microsoft.AspNetCore.Http.Features;

namespace Decisya.Bff.Proxy;

/// <summary>
/// S-5: rejects an HTTP upgrade or extended-CONNECT request on <c>/api</c> before anything
/// else in the proxy pipeline runs. YARP proxies an upgrade (e.g. WebSocket) by default, and a
/// GET upgrade would otherwise skip <see cref="ApiAntiforgeryMiddleware"/> entirely (GET is a
/// safe method), reaching Decisya.Api with no antiforgery check at all. No feature needs this
/// today.
/// </summary>
internal sealed class UpgradeRejectionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var isUpgrade = context.Features.Get<IHttpUpgradeFeature>()?.IsUpgradableRequest == true;
        var isExtendedConnect = context.Features.Get<IHttpExtendedConnectFeature>()?.IsExtendedConnect == true;

        if (isUpgrade || isExtendedConnect)
        {
            await Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Upgrade requests are not supported on this endpoint.")
                .ExecuteAsync(context)
                .ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}
