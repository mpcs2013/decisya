namespace Decisya.SharedKernel.Tenancy;

/// <summary>
/// The only sanctioned escape from the tenant query filter (issue #22, Story 6; ADR-0001).
/// A type that calls <c>IgnoreQueryFilters</c> (or another member on the bypass list) without
/// carrying this attribute fails <c>Decisya.ArchitectureTests</c>'s <c>CrossTenantQueryRule</c>.
/// A type that carries it with a blank <see cref="Justification"/> fails
/// <c>AllowCrossTenantJustificationRule</c>.
/// </summary>
/// <remarks>
/// The constructor deliberately never throws — including for a blank justification — because
/// a throwing attribute constructor breaks reflection over the whole assembly the moment one
/// instance is inspected (for example, by #24's future audit-log discovery). Emptiness is
/// enforced separately, by an architecture-time rule that reads the constructor argument via
/// <c>CustomAttributeData</c> without constructing the attribute.
/// </remarks>
/// <param name="justification">
/// A human-readable reason for the cross-tenant access (e.g. "reconciliation report for
/// platform support ticket #123"). Read back by reflection for #24's future audit log.
/// </param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AllowCrossTenantAttribute(string justification) : Attribute
{
    /// <summary>The justification text supplied to the attribute, exactly as declared.</summary>
    public string Justification { get; } = justification;
}
