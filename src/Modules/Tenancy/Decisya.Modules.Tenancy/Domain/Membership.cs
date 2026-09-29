using Decisya.SharedKernel.Tenancy;
using NodaTime;

namespace Decisya.Modules.Tenancy.Domain;

/// <summary>
/// A user's membership of a tenant, with a role (issue #21, G1 Design notes; G2). Keyed by a
/// generated <see cref="Id"/> (a version-7 GUID), because — unlike <see cref="Tenant"/> — a
/// member's identity (<see cref="UserId"/>) is not itself the tenant scope. A unique index on
/// (<c>tenant_id</c>, <c>user_id</c>) (<c>ux_memberships_tenant_user</c>) prevents two rows for
/// the same caller in the same tenant, which is what makes the JIT provisioning race in
/// <c>Application.TenantMembershipGate</c> safe (NFR-32).
/// </summary>
public sealed class Membership : ITenantScoped
{
    private Membership()
    {
        // EF Core materialization constructor.
    }

    /// <summary>Creates a new membership row for <paramref name="userId"/> in <paramref name="tenantId"/>, with <paramref name="role"/>, created at <paramref name="createdAt"/>.</summary>
    public Membership(TenantId tenantId, string userId, TenantRole role, Instant createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        UserId = userId;
        Role = role;
        CreatedAt = createdAt;
    }

    /// <summary>The membership row's own surrogate key.</summary>
    public Guid Id { get; private set; }

    /// <summary>The tenant this membership belongs to.</summary>
    public TenantId TenantId { get; }

    /// <summary>The member's own identity: the token's <c>sub</c> claim, matching <c>ICurrentCaller.UserId</c>.</summary>
    public string UserId { get; private set; } = string.Empty;

    /// <summary>The member's role within the tenant. Only <see cref="TenantRole.Owner"/> is ever assigned by #21's own code path (Story 1).</summary>
    public TenantRole Role { get; private set; }

    /// <summary>When this membership was created. Never a wall-clock read: comes from <c>IClock</c>.</summary>
    public Instant CreatedAt { get; private set; }
}
