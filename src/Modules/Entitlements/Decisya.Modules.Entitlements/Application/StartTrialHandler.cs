using Decisya.Modules.Entitlements.Domain;
using Decisya.Modules.Entitlements.Infrastructure;
using Decisya.SharedKernel.Results;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Decisya.Modules.Entitlements.Application;

/// <summary>Story 3: one trial per tenant, ever. Runs in a context scoped to the target tenant (ADR-0012).</summary>
[AllowCrossTenant("Platform-admin command on an explicit target tenant, run in a context scoped to that tenant (ADR-0012). Audit: #24 follow-up, required before #25 exposes it.")]
internal sealed class StartTrialHandler(
    ICurrentTenant currentTenant,
    DbContextOptions<EntitlementsDbContext> options,
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
            // A competing start committed first: the unique index decides the race.
            return EntitlementsErrors.TrialAlreadyUsed;
        }

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
}
