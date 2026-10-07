using Decisya.Bff.Security;
using Yarp.ReverseProxy.Forwarder;

namespace Decisya.Bff.Proxy;

/// <summary>
/// #120 G4-120-05 (B-3): the YARP forwarder's own <c>HttpClient</c> trusts the mounted root and
/// nothing else, through the handler's chain policy, when
/// <c>Bff:Backchannel:TrustedRootPath</c> is set. The BFF reaches the Api through Caddy over
/// https, and this is the client that makes that call. Unset (Development): YARP's default
/// handler, unchanged. Everything else YARP configures on the handler is kept.
/// </summary>
internal sealed class TrustedForwarderHttpClientFactory(BackchannelTrust backchannelTrust) : ForwarderHttpClientFactory
{
    protected override void ConfigureHandler(ForwarderHttpClientContext context, SocketsHttpHandler handler)
    {
        base.ConfigureHandler(context, handler);
        backchannelTrust.ApplyTo(handler);
    }
}
