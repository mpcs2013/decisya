using Decisya.Modules.Audit.Application;
using Decisya.Modules.Audit.Contracts;
using Decisya.Modules.Audit.Domain;
using Decisya.Modules.Audit.Infrastructure;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Decisya.Modules.Audit.Tests;

/// <summary>
/// G2 "NetArchTest and static rules": DI registration and the EF model (design-time model: check
/// constraints are design-time metadata). No database.
/// </summary>
[Trait("Category", "Unit")]
public class AuditModelAndRegistrationTests
{
    [Fact]
    public void AddAuditModule_registers_IAuditWriter_to_AuditWriter_scoped_and_registers_no_DbContext_or_options()
    {
        var services = new ServiceCollection();

        services.AddAuditModule();

        var descriptor = services.Should().ContainSingle().Which;
        descriptor.ServiceType.Should().Be<IAuditWriter>();
        descriptor.ImplementationType.Should().Be<AuditWriter>();
        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
        services.Should().NotContain(d =>
            d.ServiceType == typeof(AuditDbContext)
            || d.ServiceType == typeof(DbContextOptions<AuditDbContext>)
            || d.ServiceType == typeof(DbContextOptions));
    }

    [Fact]
    public void AddAuditModule_takes_no_connection_string()
    {
        typeof(AuditModule).GetMethod(nameof(AuditModule.AddAuditModule))!.GetParameters().Select(p => p.ParameterType)
            .Should().Equal(typeof(IServiceCollection));
        AuditModule.Schema.Should().Be("audit");
        AuditModule.RecordsTable.Should().Be("audit_records");
        AuditModule.TelemetryName.Should().Be("Decisya.Audit");
    }

    [Fact]
    public void Every_AuditDbContext_entity_type_maps_to_schema_audit_and_is_ITenantScoped()
    {
        using var db = new AuditDesignTimeDbContextFactory().CreateDbContext([]);

        var entityTypes = db.Model.GetEntityTypes().ToList();

        entityTypes.Should().ContainSingle();
        entityTypes.Should().OnlyContain(e => e.GetSchema() == "audit");
        entityTypes.Should().OnlyContain(e => typeof(ITenantScoped).IsAssignableFrom(e.ClrType));
        db.Model.GetDefaultSchema().Should().Be("audit");
    }

    [Fact]
    public void The_five_check_constraints_and_the_tenant_leading_index_the_design_names_exist_in_the_model()
    {
        using var db = new AuditDesignTimeDbContextFactory().CreateDbContext([]);
        var model = db.GetService<IDesignTimeModel>().Model;
        var record = model.FindEntityType(typeof(AuditRecord))!;

        record.GetCheckConstraints().Select(c => c.Name).Should().BeEquivalentTo(
            "ck_audit_records_action", "ck_audit_records_outcome", "ck_audit_records_feature_key",
            "ck_audit_records_trace_id", "ck_audit_records_actor");

        var index = record.GetIndexes().Should().ContainSingle(i => i.GetDatabaseName() == "ix_audit_records_tenant_occurred").Which;
        index.Properties.Select(p => p.Name).Should().Equal("TenantId", "OccurredAt");
    }

    [Fact]
    public void The_action_and_outcome_check_constraints_allow_only_the_closed_sets()
    {
        using var db = new AuditDesignTimeDbContextFactory().CreateDbContext([]);
        var record = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(AuditRecord))!;

        var action = record.GetCheckConstraints().Single(c => c.Name == "ck_audit_records_action").Sql;
        action.Should().Contain("'entitlements.trial.start'").And.Contain("'entitlements.override.grant'").And.Contain("'entitlements.override.revoke'");
        record.GetCheckConstraints().Single(c => c.Name == "ck_audit_records_outcome").Sql.Should().Contain("'succeeded'").And.NotContain(",");
    }

    [Fact]
    public void An_undefined_action_fails_in_the_converter_before_it_can_reach_the_database()
    {
        var act = () => AuditActionConverter.ToCode((AuditAction)99);

        act.Should().Throw<ArgumentOutOfRangeException>();
        AuditActionConverter.FromCode("entitlements.trial.start").Should().Be(AuditAction.EntitlementsTrialStart);
        var unknown = () => AuditActionConverter.FromCode("entitlements.something.else");
        unknown.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void The_migration_snapshot_matches_the_model_with_no_pending_changes()
    {
        using var db = new AuditDesignTimeDbContextFactory().CreateDbContext([]);

        db.Database.HasPendingModelChanges().Should().BeFalse("a model change needs a new migration (ef-migration skill)");
    }
}
