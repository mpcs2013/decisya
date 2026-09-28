using Decisya.Infrastructure.Persistence;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations;

/// <summary>Story 7 / G4-22-01 red fixture: maps <see cref="UnscopedEntity"/>, which is not <c>ITenantScoped</c>.</summary>
public sealed class ViolatingDbContext(DbContextOptions<ViolatingDbContext> options, ICurrentTenant currentTenant)
    : TenantDbContext(options, currentTenant)
{
    protected override void OnTenantModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UnscopedEntity>();
    }
}
