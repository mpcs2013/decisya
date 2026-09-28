using System.Net.Http.Headers;
using Yarp.ReverseProxy.Transforms;

namespace Decisya.Bff.Proxy;

/// <summary>
/// #19 G2/G3 (T-01, T-02): attaches the BFF's own access token — never a client-supplied
/// value, because <see cref="ProxyConfiguration"/>'s route already strips any incoming
/// <c>Authorization</c> header — and refuses to do so when the request's final, resolved
/// destination is not <c>https</c> outside Development (G3 point 2). Service discovery can
/// resolve <c>Bff:Api:Address</c>'s configured <c>https://decisya-api</c> to a
/// <c>services__decisya-api__https__0=http://...</c> endpoint, so the configured-string check
/// in <see cref="BffOptionsEnvironmentValidator"/> alone is not enough: this checks the actual
/// destination YARP is about to call.
/// </summary>
/// <remarks>
/// Checked on <see cref="RequestTransformContext.DestinationPrefix"/> (the resolved
/// destination's scheme and authority, e.g. <c>http://127.0.0.1:54321</c>), not
/// <c>context.ProxyRequest.RequestUri</c> — empirically, against Yarp.ReverseProxy 2.3.0, the
/// latter is still <see langword="null"/> while request transforms run; YARP only assembles it
/// afterwards, from <c>DestinationPrefix</c> plus the (possibly transform-modified) path and
/// query. Throwing here (rather than silently forwarding without the header) makes YARP's own
/// <c>HttpForwarder</c> fail request creation and return a 502, which
/// <see cref="ReplaceUpstreamErrorBodyResponseTransform"/> then turns into the generic
/// ProblemDetails G4-19-02 requires — nothing forwarded, no token attached, no upstream detail
/// leaked.
/// </remarks>
internal sealed class AttachAccessTokenRequestTransform(IHostEnvironment environment) : RequestTransform
{
    public override ValueTask ApplyAsync(RequestTransformContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var accessToken = context.HttpContext.Features.Get<ForwardedAccessTokenFeature>()?.AccessToken;
        if (string.IsNullOrEmpty(accessToken))
        {
            throw new InvalidOperationException(
                "No ForwardedAccessTokenFeature on the request; AccessTokenMiddleware must run before the proxy step attaches a bearer.");
        }

        if (!Uri.TryCreate(context.DestinationPrefix, UriKind.Absolute, out var destinationUri))
        {
            throw new InvalidOperationException("The proxy request carries no resolved, absolute destination prefix.");
        }

        if (!environment.IsDevelopment() && !string.Equals(destinationUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Refusing to attach a bearer token to a resolved destination whose scheme is not https outside Development.");
        }

        context.ProxyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return ValueTask.CompletedTask;
    }
}
