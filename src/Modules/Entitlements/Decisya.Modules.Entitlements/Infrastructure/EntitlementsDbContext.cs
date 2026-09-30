using Decisya.Infrastructure.Persistence;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Entitlements.Domain;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Decisya.Modules.Entitlements.Infrastructure;

/// <summary>
/// The Entitlements module's <see cref="TenantDbContext"/> (issue #23, module-scaffold step 6;
/// G2). Schema <c>entitlements</c>: tables <c>trial_grants</c> and <c>feature_overrides</c>.
/// </summary>
public sealed class EntitlementsDbContext(DbContextOptions<EntitlementsDbContext> options, ICurrentTenant currentTenant)
    : TenantDbContext(options, currentTenant)
{
    /// <summary>The module's schema name (also <c>EntitlementsModule.Schema</c>).</summary>
    internal const string Schema = "entitlements";

    internal DbSet<TrialGrant> TrialGrants => Set<TrialGrant>();

    internal DbSet<FeatureOverride> FeatureOverrides => Set<FeatureOverride>();

    protected override void OnTenantModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<TrialGrant>(trial =>
        {
            trial.ToTable("trial_grants", table =>
                table.HasCheckConstraint("ck_trial_grants_period", "ends_at > starts_at"));
            trial.HasKey(t => t.Id);
            trial.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
            trial.Property(t => t.TenantId).HasColumnName("tenant_id");
            trial.Property(t => t.Plan).HasColumnName("plan").HasMaxLength(16).HasConversion<string>().IsRequired();
            trial.Property(t => t.StartsAt).HasColumnName("starts_at").IsRequired();
            trial.Property(t => t.EndsAt).HasColumnName("ends_at").IsRequired();

            trial.HasIndex(t => t.TenantId)
                .IsUnique()
                .HasDatabaseName("ux_trial_grants_tenant");
        });

        modelBuilder.Entity<FeatureOverride>(feature =>
        {
            feature.ToTable("feature_overrides", table =>
                table.HasCheckConstraint("ck_feature_overrides_expiry", "expires_at IS NULL OR expires_at > granted_at"));
            feature.HasKey(o => o.Id);
            feature.Property(o => o.Id).HasColumnName("id").ValueGeneratedNever();
            feature.Property(o => o.TenantId).HasColumnName("tenant_id");
            feature.Property(o => o.FeatureKey).HasColumnName("feature_key").HasMaxLength(FeatureKey.MaxLength).IsRequired();
            feature.Property(o => o.Reason).HasColumnName("reason").HasMaxLength(FeatureOverride.MaxReasonLength).IsRequired();
            feature.Property(o => o.GrantedAt).HasColumnName("granted_at").IsRequired();
            feature.Property(o => o.ExpiresAt).HasColumnName("expires_at");

            feature.HasIndex(o => new { o.TenantId, o.FeatureKey })
                .IsUnique()
                .HasDatabaseName("ux_feature_overrides_tenant_feature");
        });
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<NodaTime.Instant>().HaveConversion<InstantConverter>();
        configurationBuilder.Properties<FeatureKey>().HaveConversion<FeatureKeyConverter>();
    }
}
