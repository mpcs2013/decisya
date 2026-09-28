using Decisya.SharedKernel.Tenancy;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations;

/// <summary>
/// A minimal, correctly tenant-scoped type used as a generic type argument by the IL fixtures
/// in this project (<see cref="UnattributedCrossTenantQuery"/> and friends), and — since it
/// carries a primary key — also mapped by <see cref="StrippedConcurrencyTokenDbContext"/>
/// (G6-22-02). The IL fixtures only need it to compile so their IL exists, not to run.
/// </summary>
public sealed class ScopedProbe(TenantId tenantId) : ITenantScoped
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    public TenantId TenantId { get; } = tenantId;

    public string Name { get; init; } = string.Empty;
}
