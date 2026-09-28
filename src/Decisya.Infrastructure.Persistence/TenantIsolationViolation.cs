namespace Decisya.Infrastructure.Persistence;

/// <summary>The specific way a <see cref="TenantIsolationException"/> was raised.</summary>
public enum TenantIsolationViolation
{
    /// <summary>
    /// The ambient <c>ICurrentTenant.Resolution</c> was <c>Invalid</c> when a query or save
    /// needed it. Raised before any SQL is sent.
    /// </summary>
    InvalidTenant,

    /// <summary>
    /// The ambient resolution was "no tenant" during a write. A write always needs a tenant;
    /// reads under "no tenant" instead return zero rows, they never throw.
    /// </summary>
    NoTenant,

    /// <summary>A newly added entity's <c>TenantId</c> was never initialized.</summary>
    UntenantedEntity,

    /// <summary>
    /// A newly added entity's <c>TenantId</c>, or an existing entity's original
    /// <c>TenantId</c>, does not match the ambient tenant.
    /// </summary>
    TenantMismatch,

    /// <summary>An existing entity's <c>TenantId</c> was changed to a different value.</summary>
    TenantChanged,

    /// <summary>
    /// A type mapped into the model does not implement <c>ITenantScoped</c>. Raised the first
    /// time the model is built; the context cannot be used at all until every mapped type is
    /// tenant-scoped.
    /// </summary>
    UnscopedEntityType,
}
