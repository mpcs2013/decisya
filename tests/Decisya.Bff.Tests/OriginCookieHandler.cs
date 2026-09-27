namespace Decisya.Bff.Tests;

/// <summary>Attaches <see cref="OriginCookieJar"/>'s cookies to every outgoing request and
/// captures every <c>Set-Cookie</c> from the response, for one origin. Test-only.</summary>
internal sealed class OriginCookieHandler : DelegatingHandler
{
    private readonly OriginCookieJar _jar;

    public OriginCookieHandler(OriginCookieJar jar)
    {
        _jar = jar;
    }

    public OriginCookieHandler(OriginCookieJar jar, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _jar = jar;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _jar.ApplyTo(request);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        _jar.CaptureFrom(response);
        return response;
    }
}
