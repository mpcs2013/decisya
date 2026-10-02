using Decisya.Modules.Tenancy.Contracts;
using Decisya.Modules.Tenancy.Infrastructure;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Decisya.Modules.Tenancy.Application;

/// <summary>
/// Issue #25, G2 D4 (S-5; ADR-0012 amendment 1 point 6): whether the tenant a scope names exists.
/// Builds its own <see cref="TenancyDbContext"/> under the scope it was given, on the Tenancy
/// connection and role, and reads through the ordinary tenant filter, so only that tenant's one row
/// can match. Mints nothing, bypasses nothing, returns a <see cref="bool"/>, and has no catch: a
/// database failure propagates.
/// </summary>
internal sealed class TenantExistence(DbContextOptions<TenancyDbContext> options) : ITenantExistence
{
    public async Task<bool> ExistsAsync(TenantResolution target, CancellationToken cancellationToken = default)
    {
        if (target.Kind != TenantResolutionKind.Tenant)
        {
            throw new ArgumentException("A tenant existence check needs a resolution of kind Tenant.", nameof(target));
        }

        await using var db = new TenancyDbContext(options, new TargetTenant(target));
        return await db.Tenants.AsNoTracking().AnyAsync(cancellationToken).ConfigureAwait(false);
    }
}
