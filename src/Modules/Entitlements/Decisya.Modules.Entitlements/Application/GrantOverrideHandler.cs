using Decisya.Modules.Entitlements.Domain;
using Decisya.Modules.Entitlements.Infrastructure;
using Decisya.SharedKernel.Results;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Decisya.Modules.Entitlements.Application;

/// <summary>Story 2: grants one feature to the target tenant regardless of plan. Runs in a context scoped to the target tenant (ADR-0012).</summary>
[AllowCrossTenant("Platform-admin command on an explicit target tenant, run in a context scoped to that tenant (ADR-0012). Audit: #24 follow-up, required before #25 exposes it.")]
internal sealed class GrantOverrideHandler(
    ICurrentTenant currentTenant,
    DbContextOptions<EntitlementsDbContext> options,
    PlanCatalog catalog,
    IClock clock,
    ILogger<GrantOverrideHandler> logger)
{
    private const string CommandName = "grant_override";
    private const string OverrideIndex = "ux_feature_overrides_tenant_feature";

    public async Task<Result> HandleAsync(GrantOverride command, CancellationToken cancellationToken)
    {
        if (currentTenant.Resolution.Kind != TenantResolutionKind.None)
        {
            return Refuse();
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

        await using var db = new EntitlementsDbContext(options, new TargetTenant(TenantResolution.For(command.TenantId)));

        try
        {
            await UpsertAsync(db, command, reason, now, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (UniqueViolation.Is(ex, OverrideIndex))
        {
            // A concurrent grant won the insert. Clear, re-query (never Reload) and save once
            // more: the winner may already be revoked, in which case the row is added afresh
            // rather than dereferenced (G3 S-3). Any further exception propagates.
            db.ChangeTracker.Clear();
            await UpsertAsync(db, command, reason, now, cancellationToken).ConfigureAwait(false);
        }

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
}
