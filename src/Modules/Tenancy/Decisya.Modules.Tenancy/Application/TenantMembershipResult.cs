namespace Decisya.Modules.Tenancy.Application;

/// <summary>The outcome of <see cref="TenantMembershipGate.EnsureAsync"/> (issue #21, Story 1, G2).</summary>
internal enum TenantMembershipResult
{
    /// <summary>The caller already has a <c>Membership</c> row in the current tenant.</summary>
    Member,

    /// <summary>The current tenant did not exist yet; it and the caller's <c>Owner</c> membership were just created (JIT).</summary>
    Provisioned,

    /// <summary>
    /// The current tenant already exists, but the caller has no <c>Membership</c> row of her
    /// own. Never auto-joined (G1 Design notes): the caller gets 403.
    /// </summary>
    Refused,
}
