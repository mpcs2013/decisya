using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// A real <see cref="ConfigurationManager{T}"/> pointed at an unreachable loopback address, for
/// the metadata-outage rows (G4-20-02, G3). G6 review (F1): a hand-rolled
/// <see cref="IConfigurationManager{T}"/>-only double (the previous
/// <c>ThrowingConfigurationManager</c>) does not reproduce what <c>JwtBearerHandler</c> actually
/// sees against a real, unreachable Keycloak — the handler only catches a fetch failure (IDX10261)
/// and returns a bare 401 when the configured manager is a real
/// <c>BaseConfigurationManager</c>. With the double, the exception propagated instead and
/// <c>UseExceptionHandler</c> produced a generic 500. Confirmed against the built API with
/// <c>http://127.0.0.1:1/realms/decisya</c> as the authority: 401, empty body,
/// <c>WWW-Authenticate: Bearer</c>.
/// </summary>
internal static class UnreachableConfigurationManager
{
    // Port 1 on loopback is not listening and never will be (unlike a randomly chosen high
    // port, which some other process could bind), so the connection is refused immediately
    // rather than timing out.
    internal const string Address = "http://127.0.0.1:1/realms/decisya/.well-known/openid-configuration";

    internal static ConfigurationManager<OpenIdConnectConfiguration> Create() =>
        new(Address, new OpenIdConnectConfigurationRetriever());
}
