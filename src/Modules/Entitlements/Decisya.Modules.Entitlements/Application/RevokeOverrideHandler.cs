using Decisya.Modules.Entitlements.Infrastructure;
using Decisya.SharedKernel.Results;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Decisya.Modules.Entitlements.Application;

/// <summary>Story 2: removes the target tenant's override (hard delete; history and audit are #24). Runs in a context scoped to the target tenant (ADR-0012).</summary>
[AllowCrossTenant("Platform-admin command on an explicit target tenant, run in a context scoped to that tenant (ADR-0012). Audit: #24 follow-up, required before #25 exposes it.")]
internal sealed class RevokeOverrideHandler(
    ICurrentTenant currentTenant,
    DbContextOptions<EntitlementsDbContext> options,
    ILogger<RevokeOverrideHandler> logger)
{
    private const string CommandName = "revoke_override";

    public async Task<Result> HandleAsync(RevokeOverride command, CancellationToken cancellationToken)
    {
        if (currentTenant.Resolution.Kind != TenantResolutionKind.None)
        {
            return Refuse();
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
                db.ChangeTracker.Clear();
            }

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
}
