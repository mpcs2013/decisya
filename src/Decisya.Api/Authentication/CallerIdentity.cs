using System.Security.Claims;

namespace Decisya.Api.Authentication;

/// <summary>
/// The caller's identity and tenant, read only from the validated principal's own claims —
/// never from <c>HttpContext.Request.Headers</c> (Story 3, #19 T-19). <see cref="TenantId"/>
/// stays the raw string; #22 parses it into the <c>TenantId</c> value type.
/// </summary>
internal sealed record CallerIdentity(string UserId, string? TenantId)
{
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

        return new CallerIdentity(subjectClaims[0].Value, tenantId);
    }
}
