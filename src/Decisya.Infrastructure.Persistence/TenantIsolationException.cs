namespace Decisya.Infrastructure.Persistence;

/// <summary>
/// Raised by <see cref="TenantDbContext"/> whenever a read, a write or the model itself would
/// cross the tenant boundary (issue #22, G1 open question 2). This is a programming-error-class
/// failure — a handler building an entity with the wrong or no <c>TenantId</c>, or a context
/// mapping an unscoped type — never an expected business outcome a caller should branch on
/// with <c>Result.Match</c>. Once a host exists (#21), an unhandled instance becomes a generic
/// <c>ProblemDetails</c> response; the message below is what a structured log records.
/// </summary>
public sealed class TenantIsolationException : InvalidOperationException
{
    /// <summary>Creates an exception for a single-entity violation (every kind but <see cref="TenantIsolationViolation.UnscopedEntityType"/>).</summary>
    public TenantIsolationException(TenantIsolationViolation violation, string entityTypeName)
        : this(violation, [entityTypeName])
    {
    }

    /// <summary>Creates an exception, optionally naming more than one offending type (<see cref="TenantIsolationViolation.UnscopedEntityType"/>).</summary>
    public TenantIsolationException(TenantIsolationViolation violation, IReadOnlyList<string> entityTypeNames)
        : base(BuildMessage(violation, entityTypeNames))
    {
        Violation = violation;
        EntityTypeNames = entityTypeNames;
    }

    /// <summary>Which kind of tenant-isolation rule was broken.</summary>
    public TenantIsolationViolation Violation { get; }

    /// <summary>
    /// The CLR full name(s) of the offending entity type(s). Never a tenant id, and never any
    /// entity value: <c>TenantIsolationException</c> messages and <see cref="Exception.ToString"/>
    /// output are safe to log verbatim (issue #22 G3, canary requirement G4-22-02).
    /// </summary>
    public IReadOnlyList<string> EntityTypeNames { get; }

    private static string BuildMessage(TenantIsolationViolation violation, IReadOnlyList<string> entityTypeNames)
    {
        var names = string.Join(", ", entityTypeNames);
        return violation switch
        {
            TenantIsolationViolation.InvalidTenant =>
                "The current tenant resolution is Invalid; no tenant-scoped query or save can run.",
            TenantIsolationViolation.NoTenant =>
                $"A write to '{names}' was attempted while the current tenant resolution is 'no tenant'; a write always needs a specific tenant.",
            TenantIsolationViolation.UntenantedEntity =>
                $"A new '{names}' entity has no TenantId assigned; every persisted entity must carry an initialized TenantId.",
            TenantIsolationViolation.TenantMismatch =>
                $"A '{names}' entity's TenantId does not match the current tenant; a row can never be read, written or removed under another tenant.",
            TenantIsolationViolation.TenantChanged =>
                $"A '{names}' entity's TenantId was changed; a row can never be moved from one tenant to another.",
            TenantIsolationViolation.UnscopedEntityType =>
                $"The following mapped entity type(s) do not implement ITenantScoped: {names}. Every type mapped into a TenantDbContext model must implement ITenantScoped.",
            TenantIsolationViolation.OwnedTypeNotInOwnersTable =>
                $"The following owned type(s) are not stored in their owner's table: {names}. An owned type must share its owner's table and schema (table splitting) or be mapped with ToJson; a separate table (for example OwnsMany, or OwnsOne(...).ToTable(...)) carries no tenant_id column of its own.",
            _ => $"Tenant isolation violation '{violation}' for: {names}.",
        };
    }
}
