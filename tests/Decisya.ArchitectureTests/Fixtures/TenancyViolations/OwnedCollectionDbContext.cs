using Decisya.Infrastructure.Persistence;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations;

/// <summary>
/// G6-22-01 red fixture: <c>OwnsMany</c> always stores its rows in a table of their own, with
/// no <c>tenant_id</c> column, so a plain key-based write can reach them once the owner is
/// merely attached.
/// </summary>
public sealed class OwnedCollectionOwner : ITenantScoped
{
    private OwnedCollectionOwner()
    {
        // EF Core materialization constructor.
    }

    public OwnedCollectionOwner(TenantId tenantId)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; }

    public List<OwnedLine> Lines { get; private set; } = [];
}

/// <summary>The owned collection element for <see cref="OwnedCollectionOwner"/>. Never <c>ITenantScoped</c> itself: an owned type is scoped through its owner's row.</summary>
public sealed class OwnedLine
{
    public string Description { get; private set; } = string.Empty;
}

public sealed class OwnedCollectionDbContext(DbContextOptions<OwnedCollectionDbContext> options, ICurrentTenant currentTenant)
    : TenantDbContext(options, currentTenant)
{
    protected override void OnTenantModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OwnedCollectionOwner>().OwnsMany(o => o.Lines);
    }
}
