using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Decisya.Infrastructure.Persistence.Tests;

/// <summary>Test-only <see cref="TenantDbContext"/> mapping a single entity, <see cref="TenantScopedProbe"/>, in its own schema (issue #22).</summary>
public sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options, ICurrentTenant currentTenant)
    : TenantDbContext(options, currentTenant)
{
    public DbSet<TenantScopedProbe> Probes => Set<TenantScopedProbe>();

    protected override void OnTenantModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("probe");
        modelBuilder.Entity<TenantScopedProbe>();
    }
}
