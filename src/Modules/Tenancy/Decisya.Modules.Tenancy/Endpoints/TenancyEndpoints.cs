using Decisya.Modules.Tenancy.Contracts;
using Decisya.Modules.Tenancy.Domain;
using Decisya.Modules.Tenancy.Infrastructure;
using Decisya.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Decisya.Modules.Tenancy.Endpoints;

/// <summary>
/// <c>GET /api/tenancy/me</c> and <c>GET /api/tenancy/members</c> (issue #21, Stories 2 and 3;
/// G2). Exposed as <c>TenancyModule.MapTenancyEndpoints</c>. Neither endpoint is
/// <c>AllowAnonymous</c>, so the API's fallback authorization policy still applies; every
/// response carries <c>Cache-Control: no-store</c>.
/// </summary>
internal static class TenancyEndpoints
{
    public static IEndpointRouteBuilder Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/tenancy");

        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context).ConfigureAwait(false);
        });

        group.MapGet("/me", GetMeAsync);
        group.MapGet("/members", GetMembersAsync).RequireAuthorization(TenancyPolicies.OwnerPolicyName);

        return endpoints;
    }

    /// <summary>
    /// Story 2: <c>None</c> gives <c>{}</c> (no "tenant" and no "membership" field, never every
    /// tenant's rows). <c>Tenant</c> gives the caller's own tenant and her own membership's
    /// role, read through the filter and matched to <see cref="ICurrentCaller.UserId"/> (BOLA).
    /// </summary>
    private static async Task<Ok<TenancyMeResponse>> GetMeAsync(
        TenancyDbContext db,
        ICurrentTenant currentTenant,
        ICurrentCaller caller,
        CancellationToken cancellationToken)
    {
        if (currentTenant.Resolution.Kind != TenantResolutionKind.Tenant)
        {
            return TypedResults.Ok(new TenancyMeResponse(null, null));
        }

        var ownRole = await db.Memberships
            .Where(m => m.UserId == caller.UserId)
            .Select(m => (TenantRole?)m.Role)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (ownRole is null)
        {
            // The membership gate already ensured a row exists before this handler runs.
            // A missing row here means the gate did not run (a wiring mistake) — fail closed,
            // never invent a tenant or a role from the claim alone (BOLA).
            return TypedResults.Ok(new TenancyMeResponse(null, null));
        }

        return TypedResults.Ok(new TenancyMeResponse(
            new TenantDto(currentTenant.Resolution.TenantId),
            new MembershipDto(ownRole.Value.ToString())));
    }

    /// <summary>Story 3: the <c>Tenancy.Owner</c> policy already refused anyone else. Ordered by <c>created_at</c>, then <c>user_id</c> (no paging in phase 0).</summary>
    private static async Task<Ok<TenancyMembersResponse>> GetMembersAsync(
        TenancyDbContext db, CancellationToken cancellationToken)
    {
        var members = await db.Memberships
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.UserId)
            .Select(m => new MemberDto(m.UserId, m.Role.ToString()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(new TenancyMembersResponse(members));
    }
}
