using Decisya.SharedKernel.Tenancy;
using NodaTime;

namespace Decisya.Modules.Entitlements.Domain;

/// <summary>
/// A tenant's one trial of Pro (issue #23, G1 Q3; G2). The unique index
/// <c>ux_trial_grants_tenant</c> allows one per tenant, ever. Activity is computed from
/// <c>IClock</c> at read time, end-exclusive; nothing expires it.
/// </summary>
internal sealed class TrialGrant : ITenantScoped
{
    /// <summary>Exact elapsed time, no calendar or time-zone arithmetic.</summary>
    public static readonly Duration TrialLength = Duration.FromDays(14);

    private TrialGrant()
    {
        // EF Core materialization constructor.
    }

    private TrialGrant(TenantId tenantId, Instant now)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        Plan = PlanId.Pro;
        StartsAt = now;
        EndsAt = now + TrialLength;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; }

    public PlanId Plan { get; private set; }

    public Instant StartsAt { get; private set; }

    public Instant EndsAt { get; private set; }

    public static TrialGrant Start(TenantId tenantId, Instant now) => new(tenantId, now);

    /// <summary>End-exclusive: active from <see cref="StartsAt"/> up to, not including, <see cref="EndsAt"/>.</summary>
    public bool IsActiveAt(Instant now) => StartsAt <= now && now < EndsAt;
}
