using System.Text;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.SharedKernel.Observability;
using Decisya.SharedKernel.Tenancy;
using NodaTime;

namespace Decisya.Modules.Entitlements.Application;

// Internal on purpose (G2 D6, G3 G4-23-02): nothing outside the module and its test assembly can
// construct a cross-tenant admin command before #24 audits it. #25 decides where they move.

/// <summary>Starts the target tenant's one 14-day Pro trial.</summary>
internal sealed record StartTrial(TenantId TenantId);

/// <summary>Grants (or re-grants) one feature to the target tenant.</summary>
internal sealed record GrantOverride(
    TenantId TenantId, FeatureKey Feature, [property: Sensitive] string Reason, Instant? ExpiresAt)
{
    // G3 G4-25-04: [Sensitive] masks only structured log state, so the synthesized ToString must
    // never render the free-text reason either (string interpolation, exception messages).
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("TenantId = ").Append(TenantId)
            .Append(", Feature = ").Append(Feature)
            .Append(", ExpiresAt = ").Append(ExpiresAt);
        return true;
    }
}

/// <summary>Removes the target tenant's override for one feature; a missing override is a success.</summary>
internal sealed record RevokeOverride(TenantId TenantId, FeatureKey Feature);
