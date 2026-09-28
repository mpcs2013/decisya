using Decisya.Infrastructure.Persistence;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Decisya.ArchitectureTests.Fixtures.TenancyCompliant;

/// <summary>Story 7 green fixture: maps only the correctly tenant-scoped <see cref="ScopedEntity"/>.</summary>
public sealed class CompliantDbContext(DbContextOptions<CompliantDbContext> options, ICurrentTenant currentTenant)
    : TenantDbContext(options, currentTenant)
{
    protected override void OnTenantModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ScopedEntity>();
    }
}
