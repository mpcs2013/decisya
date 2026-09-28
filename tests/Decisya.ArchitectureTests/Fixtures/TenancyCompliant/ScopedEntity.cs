using Decisya.SharedKernel.Tenancy;

namespace Decisya.ArchitectureTests.Fixtures.TenancyCompliant;

/// <summary>
/// Story 7 green fixture: a correctly tenant-scoped entity, mapped by
/// <see cref="CompliantDbContext"/>. Also the Done-when fixture (Story 7 scenarios 1-2):
/// removing <c>: ITenantScoped</c> here must turn <c>TenantModelRule</c> red; restoring it
/// must turn the same test green again.
/// </summary>
public sealed class ScopedEntity : ITenantScoped
{
    private ScopedEntity()
    {
        // EF Core materialization constructor.
    }

    public ScopedEntity(TenantId tenantId, string name)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        Name = name;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; }

    public string Name { get; private set; } = string.Empty;
}
