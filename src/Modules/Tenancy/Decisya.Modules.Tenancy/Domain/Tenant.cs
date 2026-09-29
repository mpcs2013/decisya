using Decisya.SharedKernel.Tenancy;
using NodaTime;

namespace Decisya.Modules.Tenancy.Domain;

/// <summary>
/// A tenant (issue #21, G1 Design notes; G2 D1). Carries no display name in phase 0: the only
/// data the platform has about a brand-new tenant at sign-in time is the <c>tenant_id</c>
/// claim itself. The primary key <b>is</b> its own <see cref="TenantId"/> — no surrogate
/// <c>Id</c> column — so <c>TenantDbContext</c>'s tenant filter and its
/// <c>SaveChanges</c> guard reduce to "a tenant sees and creates only itself" (G2 D1).
/// </summary>
public sealed class Tenant : ITenantScoped
{
    private Tenant()
    {
        // EF Core materialization constructor.
    }

    /// <summary>Creates a brand-new tenant row for <paramref name="tenantId"/>, created at <paramref name="createdAt"/> (JIT provisioning, Story 1).</summary>
    public Tenant(TenantId tenantId, Instant createdAt)
    {
        TenantId = tenantId;
        CreatedAt = createdAt;
    }

    /// <summary>The tenant's own identifier. Also its <see cref="ITenantScoped.TenantId"/> — a tenant's row is scoped to itself (G2 D1).</summary>
    public TenantId TenantId { get; }

    /// <summary>When this tenant was first provisioned (JIT, Story 1). Never a wall-clock read: comes from <c>IClock</c>.</summary>
    public Instant CreatedAt { get; private set; }
}
