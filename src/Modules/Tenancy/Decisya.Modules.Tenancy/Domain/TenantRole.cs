namespace Decisya.Modules.Tenancy.Domain;

/// <summary>
/// A member's role within her tenant (issue #21, G1 Design notes; G2 D1-D7). A value object,
/// not an entity: it has no table of its own, so <c>ITenantScoped</c> does not apply. Stored
/// as a string (<c>HasConversion&lt;string&gt;()</c> in <c>TenancyDbContext</c>), never as the
/// numeric enum value, so a column value survives a future reorder of this enum's members.
/// </summary>
/// <remarks>
/// Phase 0 (#21) assigns only <see cref="Owner"/>, through JIT provisioning (Story 1).
/// <see cref="Member"/> exists so the schema needs no breaking change the day #83 ships
/// invitations; no scenario in #21 exercises reaching it other than a seeded row.
/// </remarks>
public enum TenantRole
{
    /// <summary>The sole member of a tenant provisioned by JIT (Story 1); may list the tenant's members (Story 3).</summary>
    Owner = 1,

    /// <summary>Reserved for #83 (invitations). No #21 code path assigns this role.</summary>
    Member = 2,
}
