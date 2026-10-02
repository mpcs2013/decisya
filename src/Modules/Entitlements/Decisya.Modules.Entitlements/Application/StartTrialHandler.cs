using Decisya.Modules.Audit.Contracts;
using Decisya.Modules.Entitlements.Domain;
using Decisya.Modules.Entitlements.Infrastructure;
using Decisya.SharedKernel.Results;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Decisya.Modules.Entitlements.Application;

/// <summary>Story 3: one trial per tenant, ever. Runs in a context scoped to the target tenant (ADR-0012).</summary>
[AllowCrossTenant("Platform-admin command on an explicit target tenant, run in a context scoped to that tenant (ADR-0012). Audited in the same transaction through IAuditWriter (#24, ADR-0013).")]
internal sealed class StartTrialHandler(
    ICurrentTenant currentTenant,
    DbContextOptions<EntitlementsDbContext> options,
    ICurrentCaller caller,
    IAuditWriter audit,
    IClock clock,
    ILogger<StartTrialHandler> logger)
{
    private const string CommandName = "start_trial";
    private const string TrialTenantIndex = "ux_trial_grants_tenant";

    public async Task<Result> HandleAsync(StartTrial command, CancellationToken cancellationToken)
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
        using var activity = EntitlementsTelemetry.StartCommand("StartTrial");

        var result = await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        EntitlementsTelemetry.RecordCommand(CommandName, result);
        return result;
    }

    private async Task<Result> ExecuteAsync(StartTrial command, CancellationToken cancellationToken)
    {
        if (!command.TenantId.IsInitialized)
        {
            return EntitlementsErrors.TenantInvalid;
        }

        var now = clock.GetCurrentInstant();

        await using var db = new EntitlementsDbContext(options, new TargetTenant(TenantResolution.For(command.TenantId)));

        // ADR-0013: the change and its audit record commit together or not at all. An early
        // return or any exception leaves the transaction uncommitted, and disposal rolls it back.
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (await db.TrialGrants.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return EntitlementsErrors.TrialAlreadyUsed;
        }

        db.TrialGrants.Add(TrialGrant.Start(command.TenantId, now));

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (UniqueViolation.Is(ex, TrialTenantIndex))
        {
            // A competing start committed first: the unique index decides the race. EF rolled
            // back to its savepoint; nothing here is committed, and no audit record is written.
            return EntitlementsErrors.TrialAlreadyUsed;
        }

        await audit.AppendAsync(
            new AuditEntry(command.TenantId, AuditAction.EntitlementsTrialStart, null),
            tx.GetDbTransaction(),
            cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        EntitlementsLog.TrialStarted(logger, command.TenantId);
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
