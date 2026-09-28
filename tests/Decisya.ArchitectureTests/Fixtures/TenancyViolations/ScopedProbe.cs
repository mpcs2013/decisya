using Decisya.SharedKernel.Tenancy;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations;

/// <summary>
/// A minimal, correctly tenant-scoped type used only as a generic type argument by the IL
/// fixtures in this project (<see cref="UnattributedCrossTenantQuery"/> and friends). Never
/// mapped into a <c>DbContext</c>; these fixtures only need to compile so their IL exists, not
/// to run.
/// </summary>
public sealed class ScopedProbe(TenantId tenantId) : ITenantScoped
{
    public TenantId TenantId { get; } = tenantId;

    public string Name { get; init; } = string.Empty;
}
