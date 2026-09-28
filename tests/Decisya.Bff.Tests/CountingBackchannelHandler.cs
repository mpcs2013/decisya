using System.Net;
using System.Text;

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

    /// <summary>
    /// F2 (T-09): awaited, once set, right after a refresh grant call is dispatched but before
    /// its outcome (success or otherwise) is handed back up to <c>KeycloakTokenClient</c>. A
    /// test sets this, waits for <see cref="RefreshCallCount"/> to record the dispatch, deletes
    /// the ticket to simulate a racing back-channel logout, then completes the source — proving
    /// the refresh's later write-back cannot resurrect a session that ended while the refresh
    /// was still in flight from the caller's point of view.
    /// </summary>
    internal TaskCompletionSource<bool>? RefreshGate { get; set; }

    /// <summary>
    /// F3: when set, a refresh grant call never reaches Keycloak at all; the handler answers
    /// with this canned status and body instead. Lets a Keycloak-side failure that is hard or
    /// impossible to provoke for real (a bare 500, or a 401 <c>invalid_client</c> from a
    /// rotated client secret) be tested deterministically, without touching
    /// <see cref="ThrowOnRefresh"/>'s own "the call never even got a response" case.
    /// </summary>
    internal (HttpStatusCode StatusCode, string Body)? RefreshCannedResponse { get; set; }

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
                // F3: every value KeycloakTokenClient sends belongs in the form body (CLAUDE.md
                // "never log tokens" extends to "never put one in a URL that ends up in access
                // logs or a proxy's own request line"); a query string here would be a
                // regression a change to KeycloakTokenClient could silently introduce.
                request.RequestUri!.Query.Should().BeEmpty("a refresh grant must never carry a value in the query string");

                Interlocked.Increment(ref _refreshCallCount);
                if (ThrowOnRefresh)
                {
                    throw new HttpRequestException("Simulated Keycloak outage (refresh grant).");
                }

                if (RefreshCannedResponse is { } canned)
                {
                    return new HttpResponseMessage(canned.StatusCode)
                    {
                        Content = new StringContent(canned.Body, Encoding.UTF8, "application/json"),
                        RequestMessage = request,
                    };
                }

                if (RefreshGate is not null)
                {
                    await RefreshGate.Task.ConfigureAwait(false);
                }
            }

            // Any other grant (the login callback's own authorization_code exchange) falls
            // through untouched.
        }
        else if (request.Method == HttpMethod.Post && path.EndsWith("/protocol/openid-connect/logout", StringComparison.Ordinal))
        {
            request.RequestUri!.Query.Should().BeEmpty("an end-session call must never carry a value in the query string");

            Interlocked.Increment(ref _endSessionCallCount);
            if (ThrowOnEndSession)
            {
                throw new HttpRequestException("Simulated Keycloak outage (end-session).");
            }
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
