using Decisya.SharedKernel.Tenancy;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations;

/// <summary>Story 1 red fixture: an <c>ITenantScoped</c> entity whose <c>TenantId</c> has a public setter.</summary>
public sealed class MutableTenantEntity : ITenantScoped
{
    public TenantId TenantId { get; set; }
}
