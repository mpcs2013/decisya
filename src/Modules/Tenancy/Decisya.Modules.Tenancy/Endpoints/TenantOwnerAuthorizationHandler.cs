using Decisya.Modules.Tenancy.Domain;
using Decisya.Modules.Tenancy.Infrastructure;
using Decisya.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Decisya.Modules.Tenancy.Endpoints;

/// <summary>
/// Succeeds only when the resolution is <see cref="TenantResolutionKind.Tenant"/> <b>and</b>
/// the caller's own <see cref="Membership.Role"/> is <see cref="TenantRole.Owner"/>, read
/// through the tenant filter (issue #21, Story 3; G2; G4-21-02). <see cref="TenantResolutionKind.None"/>
/// fails (no tenant to be an Owner of), and so does <see cref="TenantRole.Member"/>. Registered
/// scoped, so it always sees the request's own <see cref="TenancyDbContext"/> and
/// <see cref="ICurrentCaller"/>, never a captured instance from an earlier request.
/// </summary>
internal sealed class TenantOwnerAuthorizationHandler(
    TenancyDbContext db,
    ICurrentTenant currentTenant,
    ICurrentCaller caller) : AuthorizationHandler<TenantOwnerRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, TenantOwnerRequirement requirement)
    {
        if (currentTenant.Resolution.Kind != TenantResolutionKind.Tenant)
        {
            return;
        }

        var isOwner = await db.Memberships
            .Where(m => m.UserId == caller.UserId)
            .Select(m => m.Role)
            .AnyAsync(role => role == TenantRole.Owner)
            .ConfigureAwait(false);

        if (isOwner)
        {
            context.Succeed(requirement);
        }
    }
}
