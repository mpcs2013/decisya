namespace Decisya.SharedKernel.Tenancy;

/// <summary>
/// The three, non-overlapping outcomes of resolving a caller's current tenant from a
/// validated identity's <c>tenant_id</c> claim (issue #22, Story 3).
/// </summary>
public enum TenantResolutionKind
{
    /// <summary>
    /// The claim was present but malformed. Never confused with <see cref="None"/>:
    /// <see cref="TenantResolution.Invalid"/> fails closed by throwing, rather than being
    /// silently treated as "show nothing" or "show everything". This is also the value of
    /// <c>default(TenantResolutionKind)</c>, so an <c>ICurrentTenant</c> nobody populated
    /// fails closed too.
    /// </summary>
    Invalid = 0,

    /// <summary>
    /// No <c>tenant_id</c> claim was present at all. Legitimate (e.g. a platform admin).
    /// A query under this resolution returns zero rows, never every tenant's rows.
    /// </summary>
    None = 1,

    /// <summary>A well-formed <c>tenant_id</c> claim resolved to a specific tenant.</summary>
    Tenant = 2,
}
