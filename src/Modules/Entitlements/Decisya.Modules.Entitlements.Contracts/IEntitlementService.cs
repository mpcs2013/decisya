namespace Decisya.Modules.Entitlements.Contracts;

/// <summary>
/// Answers "is this feature enabled for the current tenant" (issue #23, G2; ADR-0008: the
/// one implementation the <c>/bff/me</c> capability manifest and every endpoint's
/// entitlement policy share). Registered scoped; the tenant is always the ambient
/// <c>ICurrentTenant</c>, never an argument, so a caller cannot ask about another tenant.
/// </summary>
public interface IEntitlementService
{
    /// <summary>
    /// <see langword="true"/> when an unexpired override, an active trial's plan, or the Free
    /// plan grants <paramref name="feature"/> to the current tenant. Fails closed without
    /// throwing: <see langword="false"/> for <c>default(FeatureKey)</c>, a key the plan
    /// catalog does not list, and an ambient tenant resolution of <c>None</c> or
    /// <c>Invalid</c>. A database failure propagates as an exception; it is never turned
    /// into <see langword="true"/>.
    /// </summary>
    Task<bool> IsEnabledAsync(FeatureKey feature, CancellationToken cancellationToken = default);
}
