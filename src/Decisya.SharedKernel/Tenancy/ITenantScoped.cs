namespace Decisya.SharedKernel.Tenancy;

/// <summary>
/// Marks a persisted entity as belonging to exactly one tenant (<c>CLAUDE.md</c> invariant
/// 1: "every persisted aggregate implements <see cref="ITenantScoped"/> and carries
/// <c>TenantId</c>"). <c>Decisya.Infrastructure.Persistence.TenantDbContext</c> applies a
/// global query filter to every entity type that implements this interface, and rejects any
/// mapped entity type that does not (issue #22, Story 7).
/// </summary>
/// <remarks>
/// A concrete entity implements <see cref="TenantId"/> as a get-only auto-property
/// (<c>public TenantId TenantId { get; }</c>), set once by the entity's own constructor
/// (issue #22 G1 open question 3: no auto-stamp). There is deliberately no setter of any
/// visibility, including <c>init</c>: <c>TenantDbContext</c> verifies the value on every
/// write, and a mutable property would let a later change silently drop or move tenant
/// scoping. Explicit interface implementation is not supported: EF Core maps the property by
/// its declared name, <c>TenantId</c>.
/// </remarks>
public interface ITenantScoped
{
    /// <summary>The tenant this entity belongs to. Never reassigned after construction.</summary>
    TenantId TenantId { get; }
}
