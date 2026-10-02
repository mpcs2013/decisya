using Decisya.SharedKernel.Tenancy;

namespace Decisya.Modules.Tenancy.Contracts;

/// <summary>
/// Answers "does the tenant this scope names exist" (issue #25, G2, S-5; ADR-0012 amendment 1).
/// Registered scoped. The only caller today is the Entitlements admin handlers, which pass the
/// target-tenant scope they minted themselves as <c>[AllowCrossTenant]</c> types.
/// </summary>
/// <remarks>
/// The implementation builds its own Tenancy context under <c>target</c>, on the
/// Tenancy connection and role, and reads through the ordinary tenant filter. It mints no
/// resolution, bypasses no filter and returns no tenant data: only whether the one row for that
/// tenant exists. A caller that holds only its own ambient resolution can therefore ask only about
/// its own tenant.
/// </remarks>
public interface ITenantExistence
{
    /// <summary>
    /// <see langword="true"/> when a tenant row exists for <paramref name="target"/>'s tenant.
    /// A database failure propagates; it is never turned into either answer.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="target"/> is not of kind <c>Tenant</c> (<c>None</c> or <c>Invalid</c>). Thrown before any database access, with a fixed message.</exception>
    Task<bool> ExistsAsync(TenantResolution target, CancellationToken cancellationToken = default);
}
