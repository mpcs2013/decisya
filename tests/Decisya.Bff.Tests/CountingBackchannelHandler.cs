namespace Decisya.Bff.Tests;

/// <summary>
/// #19 G2: wraps the OIDC handler's real <c>Backchannel</c> handler so refresh-grant and
/// end-session POSTs against the real Testcontainers Keycloak can be counted, and optionally
/// made to fail (Story 5's "Keycloak's revocation endpoint is unreachable"), without replacing
/// Keycloak with a fake. Distinguished by the discovered endpoint path
/// (<c>/protocol/openid-connect/token</c> vs. <c>/protocol/openid-connect/logout</c>), not body
/// content — the same <c>Backchannel</c> also carries OIDC discovery, the JWKS fetch, a PAR
/// request and the login callback's own <c>authorization_code</c> exchange, none of which are
/// either call and must never be miscounted as, or made to fail like, one.
/// </summary>
internal sealed class CountingBackchannelHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    private int _refreshCallCount;
    private int _endSessionCallCount;

    internal int RefreshCallCount => _refreshCallCount;

    internal int EndSessionCallCount => _endSessionCallCount;

    /// <summary>Every refresh-grant call throws instead of reaching Keycloak (D6's "network,
    /// timeout, 5xx" case).</summary>
    internal bool ThrowOnRefresh { get; set; }

    /// <summary>Every end-session call throws instead of reaching Keycloak (Story 5's second
    /// scenario, G1 decision 2's fail-open).</summary>
    internal bool ThrowOnEndSession { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var path = request.RequestUri?.AbsolutePath ?? string.Empty;

        if (request.Method == HttpMethod.Post && path.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal))
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (body.Contains("grant_type=refresh_token", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _refreshCallCount);
                if (ThrowOnRefresh)
                {
                    throw new HttpRequestException("Simulated Keycloak outage (refresh grant).");
                }
            }

            // Any other grant (the login callback's own authorization_code exchange) falls
            // through untouched.
        }
        else if (request.Method == HttpMethod.Post && path.EndsWith("/protocol/openid-connect/logout", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _endSessionCallCount);
            if (ThrowOnEndSession)
            {
                throw new HttpRequestException("Simulated Keycloak outage (end-session).");
            }
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
