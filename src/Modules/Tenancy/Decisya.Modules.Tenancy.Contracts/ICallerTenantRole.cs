namespace Decisya.Modules.Tenancy.Contracts;

/// <summary>
/// The ambient caller's own role in the ambient tenant (issue #341, G2 D3; ADR-0005: a synchronous
/// query interface in Contracts). Registered scoped.
/// </summary>
/// <remarks>
/// Use it for authorization with deny by default: grant only on an exact match
/// (<c>role == TenantMemberRole.Owner</c>), never on "not <see cref="TenantMemberRole.Member"/>" and never on
/// "has a role". A <see langword="null"/> answer, or any value a later release adds, grants nothing.
/// </remarks>
public interface ICallerTenantRole
{
    /// <summary>
    /// The ambient caller's own role in the ambient tenant, read through the tenant filter.
    /// <see langword="null"/> when the tenant resolution is not <c>Tenant</c> (no query runs) or the
    /// caller has no membership. A database failure propagates; it is never turned into a role.
    /// </summary>
    Task<TenantMemberRole?> GetAsync(CancellationToken cancellationToken = default);
}
