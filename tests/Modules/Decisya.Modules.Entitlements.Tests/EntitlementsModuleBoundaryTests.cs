using System.Reflection;
using System.Runtime.CompilerServices;
using Decisya.Modules.Entitlements.Application;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Entitlements.Domain;
using Decisya.Modules.Entitlements.Infrastructure;
using Decisya.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using NetArchTest.Rules;

namespace Decisya.Modules.Entitlements.Tests;

/// <summary>
/// Module shape rules for issue #23 (G2 "NetArchTest and static rules"; G3 G4-23-02).
/// <para>
/// G4-23-02 closes the unaudited window of the three cross-tenant admin commands (R-1, accepted
/// by Marco on 2026-09-30): nothing outside the module and this test assembly may reach them. The
/// reachability tests below (<c>InternalsVisibleTo</c>, no ASP.NET Core and no Wolverine, no
/// <c>Endpoints</c> namespace, the exact attributed set, nothing public) may be removed or relaxed
/// only once the #24 audit record exists (G3 carry-forward C-1), and #25 lifts them deliberately.
/// </para>
/// </summary>
[Trait("Category", "Architecture")]
public class EntitlementsModuleBoundaryTests
{
    private static readonly Assembly ModuleAssembly = typeof(EntitlementsModule).Assembly;
    private static readonly Assembly ContractsAssembly = typeof(IEntitlementService).Assembly;

    private static readonly Type[] CommandTypes = [typeof(StartTrial), typeof(GrantOverride), typeof(RevokeOverride)];

    private static readonly Type[] HandlerTypes = [typeof(StartTrialHandler), typeof(GrantOverrideHandler), typeof(RevokeOverrideHandler)];

    private static IEnumerable<Type> ModuleTypes() => ModuleAssembly.GetTypes().Where(t => t.GetCustomAttribute<CompilerGeneratedAttribute>() is null);

    [Fact]
    public void The_Entitlements_module_references_no_other_modules_implementation()
    {
        var otherModules = ModuleAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("Decisya.Modules.", StringComparison.Ordinal))
            .Where(n => !n.EndsWith(".Contracts", StringComparison.Ordinal))
            .Where(n => n != ModuleAssembly.GetName().Name);

        otherModules.Should().BeEmpty();
    }

    [Fact]
    public void The_Entitlements_module_never_reads_the_system_clock_directly()
    {
        var result = Types.InAssembly(ModuleAssembly)
            .Should()
            .NotHaveDependencyOn("NodaTime.SystemClock")
            .GetResult();

        result.IsSuccessful.Should().BeTrue();
    }

    // ---- G4-23-02 ----

    [Fact]
    public void InternalsVisibleTo_is_exactly_the_module_test_assembly()
    {
        var visibleTo = ModuleAssembly.GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(a => a.AssemblyName)
            .ToList();

        visibleTo.Should().BeEquivalentTo(["Decisya.Modules.Entitlements.Tests"]);
        visibleTo.Should().ContainSingle();
    }

    [Fact]
    public void The_Entitlements_module_references_no_ASP_NET_Core_and_no_Wolverine_assembly()
    {
        var referenced = ModuleAssembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToList();

        referenced.Should().NotContain(n => n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        referenced.Should().NotContain(n => n.StartsWith("Wolverine", StringComparison.Ordinal));
        referenced.Should().NotContain(n => n.StartsWith("WolverineFx", StringComparison.Ordinal));
    }

    [Fact]
    public void The_Entitlements_project_file_declares_no_FrameworkReference_no_AspNetCore_and_no_Wolverine_package()
    {
        var csproj = File.ReadAllText(RepoPaths.Find(Path.Combine(
            "src", "Modules", "Entitlements", "Decisya.Modules.Entitlements", "Decisya.Modules.Entitlements.csproj")));

        // The csproj's own comments name these words, so match only real items.
        csproj.Should().NotContain("<FrameworkReference");
        csproj.Should().NotContain("<PackageReference Include=\"Wolverine");
        csproj.Should().NotContain("<PackageReference Include=\"Microsoft.AspNetCore");
    }

    [Fact]
    public void The_Entitlements_module_declares_no_Endpoints_namespace()
    {
        ModuleTypes()
            .Where(t => (t.Namespace ?? string.Empty).StartsWith("Decisya.Modules.Entitlements.Endpoints", StringComparison.Ordinal))
            .Should().BeEmpty();

        Directory.Exists(RepoPaths.Find(Path.Combine(
            "src", "Modules", "Entitlements", "Decisya.Modules.Entitlements", "Endpoints"))).Should().BeFalse();
    }

    [Fact]
    public void The_AllowCrossTenant_types_are_exactly_the_three_admin_handlers_and_none_is_public()
    {
        var attributed = ModuleTypes()
            .Where(t => t.GetCustomAttribute<AllowCrossTenantAttribute>(inherit: false) is not null)
            .ToList();

        attributed.Should().BeEquivalentTo(HandlerTypes);
        attributed.Should().OnlyContain(t => !t.IsVisible && !t.IsPublic && !t.IsNestedPublic);
    }

    [Fact]
    public void Each_AllowCrossTenant_justification_is_not_blank_and_names_the_24_audit_prerequisite()
    {
        foreach (var handler in HandlerTypes)
        {
            var justification = handler.GetCustomAttribute<AllowCrossTenantAttribute>()!.Justification;

            justification.Should().NotBeNullOrWhiteSpace(handler.Name);
            justification.Should().Contain("#24", handler.Name);
            justification.Should().Contain("ADR-0012", handler.Name);
        }
    }

    [Fact]
    public void The_commands_handlers_and_TargetTenant_are_internal_and_not_in_Contracts()
    {
        foreach (var type in CommandTypes.Concat(HandlerTypes).Append(typeof(TargetTenant)))
        {
            type.IsPublic.Should().BeFalse(type.Name);
            type.IsVisible.Should().BeFalse(type.Name);
            type.Assembly.Should().BeSameAs(ModuleAssembly, type.Name);
        }

        ContractsAssembly.GetTypes().Select(t => t.Name).Should().NotContain(
            ["StartTrial", "GrantOverride", "RevokeOverride", "StartTrialHandler", "GrantOverrideHandler", "RevokeOverrideHandler"]);
    }

    [Fact]
    public void No_public_type_in_the_module_or_its_Contracts_has_a_HandleAsync_method_or_a_constructor_parameter_of_a_command_type()
    {
        var publicTypes = ModuleAssembly.GetExportedTypes().Concat(ContractsAssembly.GetExportedTypes()).ToList();

        foreach (var type in publicTypes)
        {
            type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Should().NotContain(m => m.Name == "HandleAsync", type.FullName);

            type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(c => c.GetParameters())
                .Should().NotContain(p => CommandTypes.Contains(p.ParameterType), type.FullName);
        }
    }

    [Fact]
    public void The_handlers_take_the_ambient_tenant_and_the_options_and_never_the_DI_context()
    {
        foreach (var handler in HandlerTypes)
        {
            var parameters = handler.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Single().GetParameters().Select(p => p.ParameterType).ToList();

            parameters.Should().Contain(typeof(ICurrentTenant), handler.Name);
            parameters.Should().Contain(typeof(DbContextOptions<EntitlementsDbContext>), handler.Name);
            parameters.Should().NotContain(typeof(EntitlementsDbContext), handler.Name);
            parameters.Should().NotContain(typeof(IEntitlementService), handler.Name);
        }
    }

    [Fact]
    public void No_public_member_of_any_Contracts_type_has_type_TenantId_or_TenantResolution()
    {
        // G1: evaluation cannot name another tenant. Covers methods (return and parameters),
        // constructors, properties and fields, including inherited-by-declaration members.
        const BindingFlags all = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        Type[] banned = [typeof(TenantId), typeof(TenantResolution), typeof(TenantId?), typeof(TenantResolution?)];

        foreach (var type in ContractsAssembly.GetExportedTypes())
        {
            foreach (var method in type.GetMethods(all))
            {
                banned.Should().NotContain(method.ReturnType, $"{type.Name}.{method.Name} return type");
                method.GetParameters().Select(p => p.ParameterType).Should().NotContain(banned, $"{type.Name}.{method.Name} parameters");
            }

            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                ctor.GetParameters().Select(p => p.ParameterType).Should().NotContain(banned, $"{type.Name} constructor");
            }

            type.GetProperties(all).Select(p => p.PropertyType).Should().NotContain(banned, $"{type.Name} properties");
            type.GetFields(all).Select(f => f.FieldType).Should().NotContain(banned, $"{type.Name} fields");
        }
    }

    [Fact]
    public void IEntitlementService_is_declared_in_the_Contracts_assembly_and_its_only_operation_takes_a_feature_key()
    {
        typeof(IEntitlementService).Assembly.GetName().Name.Should().Be("Decisya.Modules.Entitlements.Contracts");

        var method = typeof(IEntitlementService).GetMethods().Should().ContainSingle().Which;
        method.Name.Should().Be(nameof(IEntitlementService.IsEnabledAsync));
        method.GetParameters().Select(p => p.ParameterType).Should().Equal(typeof(FeatureKey), typeof(CancellationToken));
    }

    [Fact]
    public void The_entitlement_service_contains_no_catch_so_a_database_error_can_never_become_allowed()
    {
        var source = File.ReadAllText(RepoPaths.Find(Path.Combine(
            "src", "Modules", "Entitlements", "Decisya.Modules.Entitlements", "Application", "EntitlementService.cs")));

        System.Text.RegularExpressions.Regex.IsMatch(source, @"catch\s*[({]").Should().BeFalse();
        System.Text.RegularExpressions.Regex.IsMatch(source, @"try\s*\{").Should().BeFalse();
    }

    // ---- Story 5: no DateTime or DateTimeOffset in Domain or Application ----

    [Fact]
    public void No_Domain_or_Application_type_has_a_DateTime_or_DateTimeOffset_property_field_or_parameter()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        Type[] banned = [typeof(DateTime), typeof(DateTimeOffset), typeof(DateTime?), typeof(DateTimeOffset?), typeof(TimeZoneInfo)];

        var scoped = ModuleTypes().Where(t =>
            t.Namespace == "Decisya.Modules.Entitlements.Domain" || t.Namespace == "Decisya.Modules.Entitlements.Application").ToList();
        scoped.Should().NotBeEmpty();

        foreach (var type in scoped)
        {
            type.GetProperties(all).Select(p => p.PropertyType).Should().NotContain(banned, $"{type.Name} properties");
            type.GetFields(all).Select(f => f.FieldType).Should().NotContain(banned, $"{type.Name} fields");

            foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                method.GetParameters().Select(p => p.ParameterType).Should().NotContain(banned, $"{type.Name}.{method.Name} parameters");
            }
        }
    }

    [Fact]
    public void Every_time_valued_member_of_the_aggregates_and_commands_is_an_Instant()
    {
        typeof(TrialGrant).GetProperty(nameof(TrialGrant.StartsAt))!.PropertyType.Should().Be<NodaTime.Instant>();
        typeof(TrialGrant).GetProperty(nameof(TrialGrant.EndsAt))!.PropertyType.Should().Be<NodaTime.Instant>();
        typeof(FeatureOverride).GetProperty(nameof(FeatureOverride.GrantedAt))!.PropertyType.Should().Be<NodaTime.Instant>();
        typeof(FeatureOverride).GetProperty(nameof(FeatureOverride.ExpiresAt))!.PropertyType.Should().Be<NodaTime.Instant?>();
        typeof(GrantOverride).GetProperty(nameof(GrantOverride.ExpiresAt))!.PropertyType.Should().Be<NodaTime.Instant?>();
    }

    // ---- DI registration ----

    [Fact]
    public void AddEntitlementsModule_registers_the_service_and_three_handlers_scoped_and_the_catalog_as_a_singleton()
    {
        var services = new ServiceCollection();

        services.AddEntitlementsModule("Host=x;Database=x;Username=x;Password=x");

        ServiceDescriptor Single(Type service) => services.Should().ContainSingle(d => d.ServiceType == service).Which;

        var entitlementService = Single(typeof(IEntitlementService));
        entitlementService.Lifetime.Should().Be(ServiceLifetime.Scoped);
        entitlementService.ImplementationType.Should().Be<EntitlementService>();

        foreach (var handler in HandlerTypes)
        {
            var descriptor = Single(handler);
            descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped, handler.Name);
            descriptor.ImplementationType.Should().Be(handler);
        }

        Single(typeof(PlanCatalog)).Lifetime.Should().Be(ServiceLifetime.Singleton);
        Single(typeof(EntitlementsDbContext)).Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddEntitlementsModule_does_not_pool_the_context()
    {
        var services = new ServiceCollection();

        services.AddEntitlementsModule("Host=x;Database=x;Username=x;Password=x");

        services.Should().NotContain(d => d.ServiceType.IsGenericType
            && d.ServiceType.GetGenericArguments().Contains(typeof(EntitlementsDbContext))
            && d.ServiceType.Name.Contains("Pool", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddEntitlementsModule_throws_naming_the_configuration_key_when_the_connection_string_is_missing(string? connectionString)
    {
        var act = () => new ServiceCollection().AddEntitlementsModule(connectionString!);

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("ConnectionStrings:entitlements");
    }

    // ---- EF model ----

    [Fact]
    public void Every_EntitlementsDbContext_entity_type_maps_to_schema_entitlements_and_is_ITenantScoped()
    {
        using var db = new EntitlementsDesignTimeDbContextFactory().CreateDbContext([]);

        var entityTypes = db.Model.GetEntityTypes().ToList();

        entityTypes.Should().HaveCount(2);
        entityTypes.Should().OnlyContain(e => e.GetSchema() == "entitlements");
        entityTypes.Should().OnlyContain(e => typeof(ITenantScoped).IsAssignableFrom(e.ClrType));
        db.Model.GetDefaultSchema().Should().Be("entitlements");
    }

    [Fact]
    public void The_unique_indexes_and_check_constraints_the_design_names_exist_in_the_model()
    {
        using var db = new EntitlementsDesignTimeDbContextFactory().CreateDbContext([]);
        // Check constraints are design-time model metadata, not part of the read-optimized runtime model.
        var model = db.GetService<IDesignTimeModel>().Model;
        var trial = model.FindEntityType(typeof(TrialGrant))!;
        var featureOverride = model.FindEntityType(typeof(FeatureOverride))!;

        trial.GetIndexes().Should().ContainSingle(i => i.GetDatabaseName() == "ux_trial_grants_tenant" && i.IsUnique);
        featureOverride.GetIndexes().Should().ContainSingle(i => i.GetDatabaseName() == "ux_feature_overrides_tenant_feature" && i.IsUnique);
        trial.GetCheckConstraints().Select(c => c.Name).Should().Equal("ck_trial_grants_period");
        featureOverride.GetCheckConstraints().Select(c => c.Name).Should().Equal("ck_feature_overrides_expiry");
    }

    [Fact]
    public void The_migration_snapshot_matches_the_model_with_no_pending_changes()
    {
        using var db = new EntitlementsDesignTimeDbContextFactory().CreateDbContext([]);

        db.Database.HasPendingModelChanges().Should().BeFalse(
            "a model change needs a new migration (ef-migration skill)");
    }
}
