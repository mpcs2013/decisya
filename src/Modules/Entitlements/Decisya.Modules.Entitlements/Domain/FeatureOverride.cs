using Decisya.Modules.Entitlements.Contracts;
using Decisya.SharedKernel.Observability;
using Decisya.SharedKernel.Tenancy;
using NodaTime;

namespace Decisya.Modules.Entitlements.Domain;

/// <summary>
/// An admin grant of one feature to one tenant, regardless of plan (issue #23, G1 Q4; G2).
/// Overrides only grant. One row per tenant and feature (<c>ux_feature_overrides_tenant_feature</c>).
/// </summary>
internal sealed class FeatureOverride : ITenantScoped
{
    public const int MaxReasonLength = 500;

    private FeatureOverride()
    {
        // EF Core materialization constructor.
    }

    public FeatureOverride(TenantId tenantId, FeatureKey feature, string reason, Instant grantedAt, Instant? expiresAt)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        FeatureKey = feature;
        Reason = reason;
        GrantedAt = grantedAt;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; }

    public FeatureKey FeatureKey { get; private set; }

    /// <summary>Free text from an admin; may name a customer. Never logged (G3 R-3).</summary>
    [Sensitive]
    public string Reason { get; private set; } = string.Empty;

    public Instant GrantedAt { get; private set; }

    public Instant? ExpiresAt { get; private set; }

    /// <summary>Re-grants: a new reason and expiry, and <see cref="GrantedAt"/> becomes <paramref name="now"/>.</summary>
    public void Replace(string reason, Instant? expiresAt, Instant now)
    {
        Reason = reason;
        ExpiresAt = expiresAt;
        GrantedAt = now;
    }

    /// <summary>End-exclusive at <see cref="ExpiresAt"/>; never expires when that is <see langword="null"/>.</summary>
    public bool IsActiveAt(Instant now) => ExpiresAt is null || now < ExpiresAt;
}
