using Decisya.Modules.Audit.Contracts;
using Decisya.Modules.Entitlements.Domain;
using Decisya.Modules.Entitlements.Infrastructure;
using Decisya.Modules.Tenancy.Contracts;
using Decisya.SharedKernel.Results;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Decisya.Modules.Entitlements.Application;

/// <summary>Story 2: grants one feature to the target tenant regardless of plan. Runs in a context scoped to the target tenant (ADR-0012).</summary>
[AllowCrossTenant("Platform-admin command on an explicit target tenant, run in a context scoped to that tenant (ADR-0012). Audited in the same transaction through IAuditWriter (#24, ADR-0013).")]
internal sealed class GrantOverrideHandler(
    ICurrentTenant currentTenant,
    DbContextOptions<EntitlementsDbContext> options,
    ICurrentCaller caller,
    IAuditWriter audit,
    ITenantExistence tenants,
    PlanCatalog catalog,
    IClock clock,
    ILogger<GrantOverrideHandler> logger)
{
    private const string CommandName = "grant_override";
    private const string OverrideIndex = "ux_feature_overrides_tenant_feature";

    public async Task<Result> HandleAsync(GrantOverride command, CancellationToken cancellationToken)
    {
        // ADR-0012 amendment 1, point 2 (G3 G4-25-01): the first statement, before any validation or
        // database command. A tenant-less caller alone is not enough: it must be a platform admin.
        if (currentTenant.Resolution.Kind != TenantResolutionKind.None || !caller.IsPlatformAdmin)
        {
            return Refuse();
        }

        if (!CallerActor.IsKnown(caller))
        {
            return RefuseActorUnknown();
        }

        ArgumentNullException.ThrowIfNull(command);
        using var activity = EntitlementsTelemetry.StartCommand("GrantOverride");

        var result = await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        EntitlementsTelemetry.RecordCommand(CommandName, result);
        return result;
    }

    private async Task<Result> ExecuteAsync(GrantOverride command, CancellationToken cancellationToken)
    {
        if (!command.TenantId.IsInitialized)
        {
            return EntitlementsErrors.TenantInvalid;
        }

        if (!catalog.IsKnown(command.Feature))
        {
            return EntitlementsErrors.FeatureUnknown;
        }

        var reason = command.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > FeatureOverride.MaxReasonLength)
        {
            return EntitlementsErrors.ReasonInvalid;
        }

        var now = clock.GetCurrentInstant();
        if (command.ExpiresAt is { } expiresAt && expiresAt <= now)
        {
            return EntitlementsErrors.ExpiryNotInFuture;
        }

        var target = TenantResolution.For(command.TenantId);
        if (!await tenants.ExistsAsync(target, cancellationToken).ConfigureAwait(false))
        {
            EntitlementsLog.TargetTenantNotFound(logger, CommandName, command.TenantId);
            return EntitlementsErrors.TenantNotFound;
        }

        await using var db = new EntitlementsDbContext(options, new TargetTenant(target));

        // ADR-0013: the change and its audit record commit together or not at all.
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await UpsertAsync(db, command, reason, now, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (UniqueViolation.Is(ex, OverrideIndex))
        {
            // A concurrent grant won the insert. EF rolled back to its savepoint, so the
            // transaction is usable. Clear, re-query (never Reload) and save once more: the
            // winner may already be revoked, in which case the row is added afresh rather than
            // dereferenced (G3 S-3). Any further exception propagates.
            db.ChangeTracker.Clear();
            await UpsertAsync(db, command, reason, now, cancellationToken).ConfigureAwait(false);
        }

        // Once, after the try/catch: a failed first attempt leaves no orphan record.
        await audit.AppendAsync(
            new AuditEntry(command.TenantId, AuditAction.EntitlementsOverrideGrant, command.Feature.Value),
            tx.GetDbTransaction(),
            cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        EntitlementsLog.OverrideGranted(
            logger,
            command.TenantId,
            command.Feature,
            command.ExpiresAt);
        return Result.Success();
    }

    private static async Task UpsertAsync(
        EntitlementsDbContext db, GrantOverride command, string reason, Instant now, CancellationToken cancellationToken)
    {
        var existing = await db.FeatureOverrides
            .SingleOrDefaultAsync(o => o.FeatureKey == command.Feature, cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            db.FeatureOverrides.Add(new FeatureOverride(command.TenantId, command.Feature, reason, now, command.ExpiresAt));
        }
        else
        {
            existing.Replace(reason, command.ExpiresAt, now);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private Result Refuse()
    {
        EntitlementsLog.CommandForbidden(logger, CommandName, currentTenant.Resolution.Kind.ToString());
        var result = Result.Failure(EntitlementsErrors.Forbidden);
        EntitlementsTelemetry.RecordCommand(CommandName, result);
        return result;
    }

    private Result RefuseActorUnknown()
    {
        EntitlementsLog.CommandActorUnknown(logger, CommandName);
        var result = Result.Failure(EntitlementsErrors.ActorUnknown);
        EntitlementsTelemetry.RecordCommand(CommandName, result);
        return result;
    }
}
