using Decisya.Infrastructure.Persistence;
using Decisya.Modules.Tenancy.Domain;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Decisya.Modules.Tenancy.Infrastructure;

/// <summary>
/// The Tenancy module's <see cref="TenantDbContext"/> (issue #21, module-scaffold step 6;
/// G2). Schema <c>tenancy</c>: tables <c>tenants</c> and <c>memberships</c>.
/// </summary>
public sealed class TenancyDbContext(DbContextOptions<TenancyDbContext> options, ICurrentTenant currentTenant)
    : TenantDbContext(options, currentTenant)
{
    /// <summary>The module's schema name (also <c>TenancyModule.Schema</c>).</summary>
    internal const string Schema = "tenancy";

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Membership> Memberships => Set<Membership>();

    protected override void OnTenantModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Tenant>(tenant =>
        {
            tenant.ToTable("tenants");
            tenant.HasKey(t => t.TenantId);
            tenant.Property(t => t.TenantId).HasColumnName("tenant_id");
            tenant.Property(t => t.CreatedAt).HasColumnName("created_at").IsRequired();
        });

        modelBuilder.Entity<Membership>(membership =>
        {
            membership.ToTable("memberships");
            membership.HasKey(m => m.Id);
            membership.Property(m => m.Id).HasColumnName("id");
            membership.Property(m => m.TenantId).HasColumnName("tenant_id");
            membership.Property(m => m.UserId).HasColumnName("user_id").HasMaxLength(255).IsRequired();
            membership.Property(m => m.Role).HasColumnName("role").HasMaxLength(16).HasConversion<string>().IsRequired();
            membership.Property(m => m.CreatedAt).HasColumnName("created_at").IsRequired();

            membership.HasIndex(m => new { m.TenantId, m.UserId })
                .IsUnique()
                .HasDatabaseName("ux_memberships_tenant_user");

            membership.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(m => m.TenantId)
                .HasPrincipalKey(t => t.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<NodaTime.Instant>().HaveConversion<InstantConverter>();
    }
}
