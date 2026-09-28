using Microsoft.IdentityModel.Protocols;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// Replaces <c>JwtBearerOptions.ConfigurationManager</c> (G2) so harness A never contacts the
/// network: it hands back a fixed configuration (issuer + signing keys) built from
/// <see cref="TestTokenIssuer"/>, with no discovery document and no JWKS endpoint.
/// </summary>
internal sealed class StaticConfigurationManager<T>(T configuration) : IConfigurationManager<T>
    where T : class
{
    public Task<T> GetConfigurationAsync(CancellationToken cancel) => Task.FromResult(configuration);

    public void RequestRefresh()
    {
    }
}

/// <summary>G4-20-02's red test: a configuration manager standing in for a Keycloak outage —
/// every call throws, so the handler must fail closed (401), never leak the exception.</summary>
internal sealed class ThrowingConfigurationManager<T> : IConfigurationManager<T>
    where T : class
{
    public Task<T> GetConfigurationAsync(CancellationToken cancel) =>
        throw new InvalidOperationException("Simulated metadata outage.");

    public void RequestRefresh()
    {
    }
}
