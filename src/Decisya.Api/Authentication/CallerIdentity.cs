using System.Security.Claims;

namespace Decisya.Api.Authentication;

/// <summary>
/// The caller's identity and tenant, read only from the validated principal's own claims —
/// never from <c>HttpContext.Request.Headers</c> (Story 3, #19 T-19). <see cref="TenantId"/>
/// stays the raw string; #22 parses it into the <c>TenantId</c> value type.
/// </summary>
/// <param name="UserId">The validated <c>sub</c> claim.</param>
/// <param name="TenantId">The raw <c>tenant_id</c> claim, or <see langword="null"/> when absent.</param>
/// <param name="HasPlatformAdminRole">
/// True only when every claim of type exactly <c>roles</c> is a string and at least one is
/// ordinal-equal to <c>platform-admin</c> (issue #25, G3 G4-25-01). It says nothing about the
/// tenant: <see cref="CallerContextMiddleware"/> combines it with the tenant resolution. This is
/// the one place the role is parsed.
/// </param>
/// <param name="HasMfaLevel">
/// True only when the validated principal has exactly one claim of type <c>acr</c>, of value type
/// string, ordinal-equal to <c>"2"</c> (issue #121, G3 G4-121-01 a). Absent, duplicated, numeric or
/// any other value is false. Read from the validated token only, never a header, cookie, query or
/// body, and never the ID token. This is the one place the claim is parsed.
/// </param>
internal sealed record CallerIdentity(string UserId, string? TenantId, bool HasPlatformAdminRole, bool HasMfaLevel = false)
{
    internal const string RolesClaimType = "roles";

    internal const string PlatformAdminRole = "platform-admin";

    internal const string AcrClaimType = "acr";

    /// <summary>The authentication context class reference Keycloak's step-up flow gives after the OTP (level 2).</summary>
    internal const string MfaAcrValue = "2";

    /// <summary>
    /// Returns <see langword="null"/> when <c>sub</c> is missing, empty, whitespace, or
    /// duplicated (S-2), or when <c>tenant_id</c> is duplicated, empty or whitespace (S-2). A
    /// caller with no <c>tenant_id</c> claim at all gets a <see langword="null"/>
    /// <see cref="TenantId"/> here, not a missing identity.
    /// </summary>
    internal static CallerIdentity? From(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var subjectClaims = principal.FindAll("sub").ToArray();
        if (subjectClaims.Length != 1 || string.IsNullOrWhiteSpace(subjectClaims[0].Value))
        {
            return null;
        }

        var tenantClaims = principal.FindAll("tenant_id").ToArray();
        if (tenantClaims.Length > 1)
        {
            return null;
        }

        if (tenantClaims.Length == 1 && string.IsNullOrWhiteSpace(tenantClaims[0].Value))
        {
            return null;
        }

        var tenantId = tenantClaims.Length == 1 ? tenantClaims[0].Value : null;

        return new CallerIdentity(subjectClaims[0].Value, tenantId, ReadPlatformAdminRole(principal), ReadMfaLevel(principal));
    }

    /// <summary>
    /// Exactly one claim of type <c>acr</c> (ordinal), of value type string, whose value is
    /// ordinal-equal to <c>"2"</c>. A JSON array <c>["2"]</c> in the token yields one such claim and
    /// is accepted; <c>["1","2"]</c>, <c>2</c> (a number), <c>" 2"</c>, <c>"2 "</c> and <c>"02"</c> are not.
    /// </summary>
    private static bool ReadMfaLevel(ClaimsPrincipal principal)
    {
        var acrClaims = principal.FindAll(static c => string.Equals(c.Type, AcrClaimType, StringComparison.Ordinal)).ToArray();

        return acrClaims.Length == 1
            && string.Equals(acrClaims[0].ValueType, ClaimValueTypes.String, StringComparison.Ordinal)
            && string.Equals(acrClaims[0].Value, MfaAcrValue, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads only the flat <c>roles</c> claim of the validated access token (G2 D1). Never
    /// <c>realm_access</c>, <c>resource_access</c>, <c>groups</c>, a header, a cookie, the query or
    /// the body. A non-string <c>roles</c> claim (a nested object or array) makes the whole set
    /// ambiguous, so the answer is false. The match is exact and ordinal.
    /// </summary>
    private static bool ReadPlatformAdminRole(ClaimsPrincipal principal)
    {
        var found = false;
        foreach (var claim in principal.FindAll(static c => string.Equals(c.Type, RolesClaimType, StringComparison.Ordinal)))
        {
            if (!string.Equals(claim.ValueType, ClaimValueTypes.String, StringComparison.Ordinal))
            {
                return false;
            }

            if (string.Equals(claim.Value, PlatformAdminRole, StringComparison.Ordinal))
            {
                found = true;
            }
        }

        return found;
    }
}
