using System.Diagnostics;
using Decisya.Modules.Tenancy.Domain;
using Decisya.Modules.Tenancy.Infrastructure;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Npgsql;

namespace Decisya.Modules.Tenancy.Application;

/// <summary>
/// JIT provisioning (issue #21, Story 1; G1 Design notes; G2; G3 G4-21-03, T-05, T-06, T-07).
/// Runs from <c>Endpoints.TenantMembershipMiddleware</c>, not from a handler, so it covers the
/// first request to <em>any</em> tenant endpoint and the Owner policy never runs before the
/// membership exists.
/// </summary>
/// <remarks>
/// Why this is idempotent under concurrency (NFR-32): Postgres raises <c>23505</c> only after
/// the competing transaction <b>commits</b>. If that transaction instead rolls back, the
/// waiting insert succeeds normally. So the re-read after a unique violation always sees the
/// winner. Two callers racing for one brand-new <c>tenant_id</c> therefore produce exactly one
/// <see cref="Tenant"/> and one <see cref="TenantRole.Owner"/> <see cref="Membership"/>: the
/// loser's re-read finds the winner's membership row (if same user) or the winner's tenant row
/// with no membership of her own (different user, <see cref="TenantMembershipResult.Refused"/>).
/// </remarks>
/// <remarks>
/// Order matters (fix for an intermittent NFR-32 failure test-engineer's race tests caught):
/// the tenant is checked <b>before</b> the membership, never the other way round. The winner's
/// <see cref="Tenant"/> and <see cref="Membership"/> rows are inserted in one <c>SaveChanges</c>,
/// so a transaction's writes all become visible together, atomically, under Postgres's READ
/// COMMITTED isolation. Once this method's own tenant-existence read observes the winner's
/// commit, the very next read (the membership check, for the same caller and tenant) is
/// guaranteed to observe that same commit's membership row too — so a losing, same-user caller
/// can never see "tenant exists, no membership of my own" and be wrongly <see cref="TenantMembershipResult.Refused"/>.
/// Checking membership first (the original, buggy order) had no such guarantee: a caller's own
/// membership-does-not-exist read and a separate, later tenant-exists read straddle two
/// independent snapshots, so the winner's commit could land in the gap between them.
/// </remarks>
internal sealed class TenantMembershipGate(
    TenancyDbContext db,
    ICurrentTenant currentTenant,
    ICurrentCaller caller,
    IClock clock,
    ILogger<TenantMembershipGate> logger)
{
    /// <summary>EF Core's default name for a primary key constraint declared with no explicit name (<c>PK_&lt;table&gt;</c>).</summary>
    private const string TenantsPrimaryKeyConstraintName = "PK_tenants";

    private const string MembershipsUniqueIndexName = "ux_memberships_tenant_user";

    private const string UniqueViolationSqlState = "23505";

    /// <summary>
    /// Runs the four JIT provisioning steps, tenant existence checked before membership (see
    /// the type remarks on ordering). S-1: this method runs with no
    /// explicit transaction and no ambient transaction — <c>DbContext.Database</c>'s own
    /// implicit per-<c>SaveChanges</c> transaction is enough, and an outer transaction around this call
    /// (a future Wolverine outbox) would turn the re-read below into <c>25P02</c> instead of a
    /// clean retry. There is deliberately no retry loop beyond the one re-read: see the type
    /// remarks for why a single re-read is always correct.
    /// </summary>
    public async Task<TenantMembershipResult> EnsureAsync(CancellationToken cancellationToken)
    {
        Debug.Assert(db.Database.CurrentTransaction is null, "TenantMembershipGate must never run inside an explicit or ambient transaction (S-1, T-07).");

        if (await db.Tenants.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            // The tenant already exists. Its Owner membership, if any, was inserted in the same
            // SaveChanges as the tenant row, so this second read is guaranteed to see it too
            // (see the type remarks on ordering) — never auto-join (G1: an existing tenant is
            // never joined by a caller who has no membership of her own).
            if (await HasOwnMembershipAsync(cancellationToken).ConfigureAwait(false))
            {
                return TenantMembershipResult.Member;
            }

            TenantMembershipGateLog.MembershipRefused(logger);
            TenancyModule.Meter.CreateCounter<long>("decisya.tenancy.memberships.refused").Add(1);
            return TenantMembershipResult.Refused;
        }

        var tenantId = currentTenant.Resolution.TenantId;
        var now = clock.GetCurrentInstant();

        db.Tenants.Add(new Tenant(tenantId, now));
        db.Memberships.Add(new Membership(tenantId, caller.UserId, TenantRole.Owner, now));

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsRaceLossOnProvisioning(ex))
        {
            db.ChangeTracker.Clear();

            var wonAfterAll = await HasOwnMembershipAsync(cancellationToken).ConfigureAwait(false);
            if (wonAfterAll)
            {
                return TenantMembershipResult.Member;
            }

            TenantMembershipGateLog.MembershipRefusedAfterRace(logger);
            TenancyModule.Meter.CreateCounter<long>("decisya.tenancy.memberships.refused").Add(1);
            return TenantMembershipResult.Refused;
        }

        TenantMembershipGateLog.TenantProvisioned(logger);
        TenancyModule.Meter.CreateCounter<long>("decisya.tenancy.tenants.provisioned").Add(1);
        return TenantMembershipResult.Provisioned;
    }

    private Task<bool> HasOwnMembershipAsync(CancellationToken cancellationToken) =>
        db.Memberships.AnyAsync(m => m.UserId == caller.UserId, cancellationToken);

    /// <summary>
    /// <see langword="true"/> only for a <c>23505</c> unique violation on exactly the two
    /// constraints this provisioning step could ever race on (G4-21-03: "the catch matches
    /// <c>PostgresException.SqlState == "23505"</c> <b>and</b> <c>ConstraintName</c> in {the
    /// <c>tenants</c> PK, <c>ux_memberships_tenant_user</c>}. Any other exception propagates
    /// as the generic 500.").
    /// </summary>
    private static bool IsRaceLossOnProvisioning(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: UniqueViolationSqlState } pg &&
        pg.ConstraintName is TenantsPrimaryKeyConstraintName or MembershipsUniqueIndexName;
}
