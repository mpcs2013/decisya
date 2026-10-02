using Decisya.Modules.Audit.Contracts;
using Decisya.Modules.Entitlements.Infrastructure;
using Decisya.SharedKernel.Results;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Decisya.Modules.Entitlements.Application;

/// <summary>Story 2: removes the target tenant's override (hard delete; the audit record is appended in the same transaction, #24). Runs in a context scoped to the target tenant (ADR-0012).</summary>
[AllowCrossTenant("Platform-admin command on an explicit target tenant, run in a context scoped to that tenant (ADR-0012). Audited in the same transaction through IAuditWriter (#24, ADR-0013).")]
internal sealed class RevokeOverrideHandler(
    ICurrentTenant currentTenant,
    DbContextOptions<EntitlementsDbContext> options,
    ICurrentCaller caller,
    IAuditWriter audit,
    ILogger<RevokeOverrideHandler> logger)
{
    private const string CommandName = "revoke_override";

    public async Task<Result> HandleAsync(RevokeOverride command, CancellationToken cancellationToken)
    {
        if (currentTenant.Resolution.Kind != TenantResolutionKind.None)
        {
            return Refuse();
        }

        if (!CallerActor.IsKnown(caller))
        {
            return RefuseActorUnknown();
        }

        ArgumentNullException.ThrowIfNull(command);
        using var activity = EntitlementsTelemetry.StartCommand("RevokeOverride");

        var result = await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        EntitlementsTelemetry.RecordCommand(CommandName, result);
        return result;
    }

    private async Task<Result> ExecuteAsync(RevokeOverride command, CancellationToken cancellationToken)
    {
        if (!command.TenantId.IsInitialized)
        {
            return EntitlementsErrors.TenantInvalid;
        }

        // Any well-formed key is accepted, so an override for a key later removed from the
        // catalog can still be revoked.
        if (!command.Feature.IsInitialized)
        {
            return EntitlementsErrors.FeatureUnknown;
        }

        await using var db = new EntitlementsDbContext(options, new TargetTenant(TenantResolution.For(command.TenantId)));

        // ADR-0013: the change and its audit record commit together or not at all.
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var existing = await db.FeatureOverrides
            .SingleOrDefaultAsync(o => o.FeatureKey == command.Feature, cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            db.FeatureOverrides.Remove(existing);

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                // A concurrent revoke already deleted it: the outcome is the one asked for.
                // EF rolled back to its savepoint, so the transaction is usable.
                db.ChangeTracker.Clear();
            }
        }

        // Every succeeded revoke is audited, including one with no override to remove (G1 Story 2).
        await audit.AppendAsync(
            new AuditEntry(command.TenantId, AuditAction.EntitlementsOverrideRevoke, command.Feature.Value),
            tx.GetDbTransaction(),
            cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            EntitlementsLog.OverrideRevoked(logger, command.TenantId, command.Feature);
        }

        return Result.Success();
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
