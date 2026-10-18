using Ostium.Authentication.Jwt.Testing;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// Decisya's constants for the token issuer that comes from <c>Ostium.Authentication.Jwt.Testing</c>
/// (#164 G2 D4). Constants and a factory only: no claim-shaping adapter. A test that wants a tenant,
/// roles or an <c>acr</c> passes them as claims to the package's own methods.
/// </summary>
internal static class DecisyaTestTokens
{
    internal const string Issuer = "https://issuer.test/realms/decisya";

    /// <summary>The API's audience. A test pins it to <c>src/Decisya.Api/appsettings.json</c>.</summary>
    internal const string Audience = "decisya-api";

    internal const string DevAliceTenantId = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    internal static TestTokenIssuer Create() => new(Issuer, Audience);

    /// <summary>Sets <c>tenant_id</c> on a claim set (default: the Dev Alice tenant) and returns it.</summary>
    internal static IDictionary<string, object> WithTenant(IDictionary<string, object> claims, string tenantId = DevAliceTenantId)
    {
        ArgumentNullException.ThrowIfNull(claims);
        claims["tenant_id"] = tenantId;
        return claims;
    }

    /// <summary>
    /// The <c>tenant_id</c> claim as an additional-claims dictionary for
    /// <c>IssueAccessToken</c>; <see langword="null"/> gives no claim at all.
    /// </summary>
    internal static Dictionary<string, object> Tenant(string? tenantId)
    {
        var claims = new Dictionary<string, object>(StringComparer.Ordinal);
        if (tenantId is not null)
        {
            claims["tenant_id"] = tenantId;
        }

        return claims;
    }
}
