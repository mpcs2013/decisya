using Decisya.Modules.Tenancy.Contracts;
using Decisya.Modules.Tenancy.Domain;
using Decisya.Modules.Tenancy.Infrastructure;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Decisya.Modules.Tenancy.Application;

/// <summary>
/// <see cref="ICallerTenantRole"/> (issue #341, G2 D3; G3 M341-01 (d)). Reads the caller's own membership
/// through <see cref="TenancyDbContext"/>'s tenant filter and runs no query unless the ambient resolution is
/// <c>Tenant</c>. The tenant and the caller are read at use time, never in the constructor (R16). Has no
/// catch: a database failure propagates, and is never turned into a role.
/// </summary>
internal sealed class CallerTenantRole(TenancyDbContext db, ICurrentTenant tenant, ICurrentCaller caller) : ICallerTenantRole
{
    public async Task<TenantMemberRole?> GetAsync(CancellationToken cancellationToken = default)
    {
        if (tenant.Resolution.Kind != TenantResolutionKind.Tenant)
        {
            return null;
        }

        var userId = caller.UserId;
        var role = await db.Memberships
            .Where(m => m.UserId == userId)
            .Select(m => (TenantRole?)m.Role)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // Mapped by number, so the two enums stay value-for-value mirrors (a Tenancy rule asserts it).
        return role is { } value ? (TenantMemberRole)(int)value : null;
    }
}
