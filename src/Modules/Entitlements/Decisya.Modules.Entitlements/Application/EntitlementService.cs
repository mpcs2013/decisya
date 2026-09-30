using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Entitlements.Domain;
using Decisya.Modules.Entitlements.Infrastructure;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Decisya.Modules.Entitlements.Application;

/// <summary>
/// <see cref="IEntitlementService"/> (issue #23, G2 "Evaluation"). Uses the DI context, so every
/// read goes through the ambient tenant filter and no query names a tenant. Fails closed and
/// never catches: a database exception propagates and is never turned into "allowed" (G3 T-07).
/// </summary>
internal sealed class EntitlementService(
    EntitlementsDbContext db, ICurrentTenant currentTenant, PlanCatalog catalog, IClock clock) : IEntitlementService
{
    public async Task<bool> IsEnabledAsync(FeatureKey feature, CancellationToken cancellationToken = default)
    {
        // Value is read only for a known key: never for default or unknown (G3 T-06).
        var known = catalog.IsKnown(feature);
        var (granted, source) = known
            ? await EvaluateAsync(feature, cancellationToken).ConfigureAwait(false)
            : (false, EntitlementsTelemetry.SourceNone);

        EntitlementsTelemetry.RecordEvaluation(known ? feature.Value : "unknown", granted, source);
        return granted;
    }

    private async Task<(bool Granted, string Source)> EvaluateAsync(FeatureKey feature, CancellationToken cancellationToken)
    {
        // None and Invalid are denied before any query; Invalid would throw inside the context.
        if (currentTenant.Resolution.Kind != TenantResolutionKind.Tenant)
        {
            return (false, EntitlementsTelemetry.SourceNone);
        }

        // Overrides and trials only grant, so a Free key needs no query.
        if (catalog.Includes(PlanId.Free, feature))
        {
            return (true, EntitlementsTelemetry.SourcePlan);
        }

        var now = clock.GetCurrentInstant();

        var featureOverride = await db.FeatureOverrides.AsNoTracking()
            .SingleOrDefaultAsync(o => o.FeatureKey == feature, cancellationToken).ConfigureAwait(false);
        if (featureOverride is not null && featureOverride.IsActiveAt(now))
        {
            return (true, EntitlementsTelemetry.SourceOverride);
        }

        var trial = await db.TrialGrants.AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (trial is not null && trial.IsActiveAt(now) && catalog.Includes(trial.Plan, feature))
        {
            return (true, EntitlementsTelemetry.SourceTrial);
        }

        return (false, EntitlementsTelemetry.SourceNone);
    }
}
