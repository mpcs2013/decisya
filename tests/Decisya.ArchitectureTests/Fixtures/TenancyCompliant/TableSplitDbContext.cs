using Decisya.Infrastructure.Persistence;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Decisya.ArchitectureTests.Fixtures.TenancyCompliant;

/// <summary>
/// G6-22-01 green fixture: a default <c>OwnsOne</c> table-splits into the owner's own row, so
/// <see cref="TableSplitOwner"/>'s <c>tenant_id</c> covers <see cref="OwnedDetail"/> too, with
/// no separate table and no separate key-based write path.
/// </summary>
public sealed class TableSplitOwner : ITenantScoped
{
    private TableSplitOwner()
    {
        // EF Core materialization constructor.
    }

    public TableSplitOwner(TenantId tenantId)
    {
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; }

    public OwnedDetail Detail { get; private set; } = new();
}

/// <summary>The table-split owned type for <see cref="TableSplitOwner"/>. Never <c>ITenantScoped</c> itself: it shares the owner's row.</summary>
public sealed class OwnedDetail
{
    public string Note { get; private set; } = string.Empty;
}

public sealed class TableSplitDbContext(DbContextOptions<TableSplitDbContext> options, ICurrentTenant currentTenant)
    : TenantDbContext(options, currentTenant)
{
    protected override void OnTenantModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TableSplitOwner>().OwnsOne(o => o.Detail);
    }
}
