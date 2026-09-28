using Microsoft.AspNetCore.Antiforgery;

namespace Decisya.Bff.Security;

/// <summary>
/// D4: the double-submit antiforgery validation body, shared by <see cref="AntiforgeryEndpointFilter"/>
/// (<c>/bff</c>) and <c>Decisya.Bff.Proxy.ApiAntiforgeryMiddleware</c> (<c>/api</c>) — YARP
/// endpoints never run endpoint filters, so a proxy middleware is the only place the <c>/api</c>
/// side of the check can live (G2). Both call this, so the two surfaces share one rule: the
/// same safe-method allow-list, the same header-required-before-form-read guard (S-2, T-18).
/// </summary>
internal static class AntiforgeryCheck
{
    /// <summary>S-2: an allow-list of safe methods, not a deny-list, so a verb added later is
    /// mutating by default instead of silently skipping the check.</summary>
    internal static bool IsMutatingMethod(string method) =>
        !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method)
            || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method));

    /// <summary>Returns <see langword="true"/> when the request may proceed: either it is not
    /// mutating, or it carries a valid antiforgery token pair.</summary>
    internal static async Task<bool> ValidateAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!IsMutatingMethod(context.Request.Method))
        {
            return true;
        }

        // S-2: reject before IAntiforgery ever reads the request body as a form — otherwise a
        // mutating request with no header at all still has its body consumed for nothing, and
        // YARP would then forward an already-drained body.
        if (!context.Request.Headers.ContainsKey(AntiforgeryCookieNames.HeaderName))
        {
            return false;
        }

        var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }
}
