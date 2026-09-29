using System.Reflection;
using Decisya.Modules.Tenancy.Domain;
using Decisya.Modules.Tenancy.Infrastructure;
using Decisya.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// Story 4 and Story 5 scenario 1 (G2): appends terminal, test-only branches after the real
/// <c>Decisya.Api</c> pipeline, so every route still passes through <c>UseExceptionHandler</c>,
/// <c>UseAuthentication</c> and <c>UseAuthorization</c> exactly as any real endpoint would
/// (neither path is a mapped endpoint, so the fallback policy applies to them the same way it
/// would to an unknown path — and <c>TenantMembershipMiddleware</c>'s gate, which only runs for
/// a matched <c>Endpoint</c>, never runs for any of them either).
/// </summary>
internal sealed class TestOnlyEndpointsStartupFilter(string canary) : IStartupFilter
{
    internal const string ThrowPath = "/__test/throw";
    internal const string ConnectionPath = "/__test/connection";

    /// <summary>
    /// Issue #21, G2's own design note: "#21 has no update path of its own, so G5 raises a real
    /// conflict through a test-only endpoint... a detached Update of another tenant's
    /// Membership, which the #22 concurrency token turns into zero rows." Query string
    /// <c>membershipId</c> names an already-persisted <see cref="Membership"/> row belonging to
    /// a tenant other than the caller's own. The stub's <see cref="Membership.TenantId"/> is the
    /// caller's own (real) ambient tenant — exactly like
    /// <c>TenantWriteGuardTests</c>' detached-Update stub (#22) — so <c>TenantDbContext</c>'s own
    /// guard (<c>TenantMismatch</c>) never fires and EF's own optimistic-concurrency check is
    /// what raises <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>,
    /// which <c>ConcurrencyConflictExceptionHandler</c> then turns into the generic 404.
    /// <see cref="Membership.Id"/> has no public way to be set to an already-minted id (by
    /// design: an id is never chosen by a caller), so this reaches it through the property's own
    /// private setter via reflection — a test-only technique, not a product change.
    /// </summary>
    internal const string ConcurrencyConflictPath = "/__test/concurrency-conflict";

    private static readonly PropertyInfo MembershipIdProperty = typeof(Membership)
        .GetProperty(nameof(Membership.Id))
        ?? throw new InvalidOperationException("Membership.Id was not found by reflection.");

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        next(app);

        app.Map(ThrowPath, branch => branch.Run(_ => throw new InvalidOperationException(canary)));
        app.Map(ConnectionPath, branch => branch.Run(async context =>
        {
            await context.Response.WriteAsync(
                $"{context.Request.Scheme}|{context.Connection.RemoteIpAddress}", context.RequestAborted);
        }));
        app.Map(ConcurrencyConflictPath, branch => branch.Run(async context =>
        {
            var membershipId = Guid.Parse(context.Request.Query["membershipId"].ToString());
            var attackerTenant = context.RequestServices.GetRequiredService<ICurrentTenant>().Resolution.TenantId;
            var db = context.RequestServices.GetRequiredService<TenancyDbContext>();

            var stub = new Membership(attackerTenant, canary, TenantRole.Member, SystemClock.Instance.GetCurrentInstant());
            MembershipIdProperty.SetValue(stub, membershipId);

            db.Memberships.Update(stub);
            await db.SaveChangesAsync(context.RequestAborted);
        }));
    };
}
