using Decisya.Infrastructure.Persistence;
using Decisya.Modules.Audit.Contracts;
using Decisya.Modules.Audit.Domain;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Decisya.Modules.Audit.Infrastructure;

/// <summary>
/// The Audit module's <see cref="TenantDbContext"/> (issue #24, module-scaffold step 6; G2).
/// Schema <c>audit</c>, one table: <c>audit_records</c>. Public only because the migrator builds it;
/// there is no public <c>DbSet</c> and <see cref="AuditRecord"/> is internal, so nothing outside the
/// module can name the entity to read it (G1 Story 5).
/// </summary>
public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options, ICurrentTenant currentTenant)
    : TenantDbContext(options, currentTenant)
{
    /// <summary>The module's schema name (also <c>AuditModule.Schema</c>).</summary>
    internal const string Schema = AuditModule.Schema;

    internal DbSet<AuditRecord> Records => Set<AuditRecord>();

    protected override void OnTenantModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<AuditRecord>(record =>
        {
            record.ToTable(AuditModule.RecordsTable, table =>
            {
                table.HasCheckConstraint(
                    "ck_audit_records_action",
                    $"action IN ('{AuditActionConverter.TrialStart}', '{AuditActionConverter.OverrideGrant}', '{AuditActionConverter.OverrideRevoke}')");
                table.HasCheckConstraint("ck_audit_records_outcome", $"outcome IN ('{AuditOutcomeConverter.Succeeded}')");
                table.HasCheckConstraint(
                    "ck_audit_records_feature_key", $"(action = '{AuditActionConverter.TrialStart}') = (feature_key IS NULL)");
                table.HasCheckConstraint("ck_audit_records_trace_id", "trace_id IS NULL OR trace_id ~ '^[0-9a-f]{32}$'");
                table.HasCheckConstraint("ck_audit_records_actor", "length(btrim(actor_user_id)) > 0");
            });

            record.HasKey(r => r.Id);
            record.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            record.Property(r => r.TenantId).HasColumnName("tenant_id");
            record.Property(r => r.OccurredAt).HasColumnName("occurred_at").IsRequired();
            record.Property(r => r.ActorUserId).HasColumnName("actor_user_id").HasMaxLength(255).IsRequired();
            record.Property(r => r.Action).HasColumnName("action").HasMaxLength(64).IsRequired();
            record.Property(r => r.Outcome).HasColumnName("outcome").HasMaxLength(16).IsRequired();
            record.Property(r => r.FeatureKey).HasColumnName("feature_key").HasMaxLength(64);
            record.Property(r => r.TraceId).HasColumnName("trace_id").HasMaxLength(32);

            record.HasIndex(r => new { r.TenantId, r.OccurredAt }).HasDatabaseName("ix_audit_records_tenant_occurred");
        });
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<NodaTime.Instant>().HaveConversion<InstantConverter>();
        configurationBuilder.Properties<AuditAction>().HaveConversion<AuditActionConverter>();
        configurationBuilder.Properties<AuditOutcome>().HaveConversion<AuditOutcomeConverter>();
    }
}
