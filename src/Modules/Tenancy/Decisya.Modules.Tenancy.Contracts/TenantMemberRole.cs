namespace Decisya.Modules.Tenancy.Contracts;

/// <summary>
/// A member's role in her space, as other modules may see it (issue #341, G2). Mirrors Tenancy's own
/// role enum name for name and value for value (a Tenancy architecture rule asserts it), so a new Tenancy
/// role is a reviewed change here too. Persistence of the role stays Tenancy's.
/// </summary>
public enum TenantMemberRole
{
    /// <summary>The space's owner.</summary>
    Owner = 1,

    /// <summary>A member of the space who is not its owner.</summary>
    Member = 2,
}
