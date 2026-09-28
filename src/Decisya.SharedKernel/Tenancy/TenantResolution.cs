namespace Decisya.SharedKernel.Tenancy;

/// <summary>
/// The result of resolving the current caller's tenant from a validated identity's
/// <c>tenant_id</c> claim (issue #22, Story 3): exactly one of <see cref="TenantResolutionKind.Invalid"/>,
/// <see cref="TenantResolutionKind.None"/> or <see cref="TenantResolutionKind.Tenant"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>default(TenantResolution)</c> is <see cref="Invalid"/>. An <c>ICurrentTenant</c> that
/// nobody populated (missing middleware, a background job that forgot to set one) therefore
/// fails closed with an exception when its <see cref="TenantId"/> is read or when a
/// <c>Decisya.Infrastructure.Persistence.TenantDbContext</c> query or save runs, rather than
/// silently behaving like "no tenant" or, worse, like a specific tenant.
/// </para>
/// <para>
/// The host middleware that populates this from <c>CallerIdentity.TenantId</c> is #21's
/// responsibility, not #22's; this type only defines the three outcomes and how a raw claim
/// value maps to them.
/// </para>
/// </remarks>
public readonly struct TenantResolution : IEquatable<TenantResolution>
{
    private readonly TenantId _tenantId;

    private TenantResolution(TenantResolutionKind kind, TenantId tenantId)
    {
        Kind = kind;
        _tenantId = tenantId;
    }

    /// <summary>Which of the three outcomes this resolution represents.</summary>
    public TenantResolutionKind Kind { get; }

    /// <summary>
    /// The resolved tenant. Only valid when <see cref="Kind"/> is <see cref="TenantResolutionKind.Tenant"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="Kind"/> is not <see cref="TenantResolutionKind.Tenant"/>.</exception>
    public TenantId TenantId => Kind == TenantResolutionKind.Tenant
        ? _tenantId
        : throw new InvalidOperationException(
            $"TenantResolution.TenantId is only valid when Kind is {TenantResolutionKind.Tenant}; this resolution's Kind is {Kind}.");

    /// <summary>
    /// A malformed or unparsable claim, or an <c>ICurrentTenant</c> nobody populated
    /// (<c>default(TenantResolution)</c>). Fails closed: never treated as <see cref="NoTenant"/>
    /// and never treated as any specific tenant.
    /// </summary>
    public static TenantResolution Invalid => default;

    /// <summary>
    /// No <c>tenant_id</c> claim was present at all (legitimate, e.g. a platform admin). A
    /// query under this resolution returns zero rows, never every tenant's rows.
    /// </summary>
    public static TenantResolution NoTenant { get; } = new(TenantResolutionKind.None, default);

    /// <summary>A specific, resolved tenant.</summary>
    /// <exception cref="ArgumentException"><paramref name="tenantId"/> is not initialized.</exception>
    public static TenantResolution For(TenantId tenantId)
    {
        if (!tenantId.IsInitialized)
        {
            throw new ArgumentException(
                "A TenantResolution.For tenant must be an initialized TenantId, never default(TenantId).",
                nameof(tenantId));
        }

        return new TenantResolution(TenantResolutionKind.Tenant, tenantId);
    }

    /// <summary>
    /// Resolves a raw <c>tenant_id</c> claim value: <see langword="null"/> (no claim present)
    /// gives <see cref="NoTenant"/>; a value <see cref="Tenancy.TenantId.TryParse(ReadOnlySpan{char}, out TenantId)"/>
    /// accepts gives <see cref="For(TenantId)"/>; anything else — empty, whitespace,
    /// <c>"not-a-guid"</c>, the all-zero GUID, or a legacy <c>0x…</c>/<c>+…</c> compatibility
    /// form — gives <see cref="Invalid"/>. A missing claim is never confused with a malformed
    /// one.
    /// </summary>
    public static TenantResolution FromClaim(string? claimValue)
    {
        if (claimValue is null)
        {
            return NoTenant;
        }

        return TenantId.TryParse(claimValue.AsSpan(), out var tenantId)
            ? For(tenantId)
            : Invalid;
    }

    public bool Equals(TenantResolution other) =>
        Kind == other.Kind && (Kind != TenantResolutionKind.Tenant || _tenantId.Equals(other._tenantId));

    public override bool Equals(object? obj) => obj is TenantResolution other && Equals(other);

    public override int GetHashCode() =>
        Kind == TenantResolutionKind.Tenant ? HashCode.Combine(Kind, _tenantId) : HashCode.Combine(Kind);

    public override string ToString() => Kind switch
    {
        TenantResolutionKind.None => "None",
        TenantResolutionKind.Tenant => $"Tenant({_tenantId})",
        _ => "Invalid",
    };

    public static bool operator ==(TenantResolution left, TenantResolution right) => left.Equals(right);

    public static bool operator !=(TenantResolution left, TenantResolution right) => !left.Equals(right);
}
