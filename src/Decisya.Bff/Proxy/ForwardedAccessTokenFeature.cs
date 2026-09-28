namespace Decisya.Bff.Proxy;

/// <summary>
/// #19 G2: carries the access token <see cref="AccessTokenMiddleware"/> decided to forward,
/// from the proxy pipeline to the request transform that attaches it as the outgoing
/// <c>Authorization</c> header. Never logged; the SPA never sees this request either way.
/// </summary>
internal sealed record ForwardedAccessTokenFeature(string AccessToken);
