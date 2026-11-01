using Decisya.SharedKernel.Tenancy;

namespace Decisya.ArchitectureTests.Fixtures.TenancyCompliant.ConstructionTimeReads;

// R16 green fixtures (#231, G3 G4-231-09): the approved use-time pattern.

/// <summary>Stores the <c>ICurrentTenant</c> reference in the constructor and reads it in a method.</summary>
public sealed class StoresTheReference
{
    private readonly ICurrentTenant _tenant;

    public StoresTheReference(ICurrentTenant tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        _tenant = tenant;
    }

    public TenantResolution Read() => _tenant.Resolution;
}

/// <summary>Builds a <see cref="Lazy{T}"/> over a lambda that reads the tenant later.</summary>
public sealed class LazyOverALambda
{
    private readonly Lazy<TenantResolution> _resolution;

    public LazyOverALambda(ICurrentTenant tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        _resolution = new Lazy<TenantResolution>(() => tenant.Resolution);
    }

    public TenantResolution Value => _resolution.Value;
}
