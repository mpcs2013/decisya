using System.Reflection;
using System.Runtime.CompilerServices;
using Decisya.Modules.Admin.Contracts;
using Decisya.Modules.Entitlements.Contracts.Admin;
using Decisya.SharedKernel.Tenancy;
using NetArchTest.Rules;

namespace Decisya.Modules.Admin.Tests;

/// <summary>
/// The Admin module rules of issue #25 (G2 "NetArchTest and static rules"; G3 G4-25-04): the module
/// persists nothing, reaches other modules only through their Contracts, and its endpoints call only
/// <see cref="IEntitlementAdminCommands"/>. The "mints nothing" IL scan lives in
/// <c>Decisya.ArchitectureTests</c> (<c>AdminMintsNothingTests</c>).
/// </summary>
[Trait("Category", "Architecture")]
public class AdminModuleBoundaryTests
{
    private static readonly Assembly ModuleAssembly = typeof(AdminModule).Assembly;
    private static readonly Assembly ContractsAssembly = typeof(OverrideGrantRequest).Assembly;

    private static IEnumerable<Type> ModuleTypes() =>
        ModuleAssembly.GetTypes().Where(t => t.GetCustomAttribute<CompilerGeneratedAttribute>() is null);

    [Fact]
    public void The_Admin_module_references_no_other_modules_implementation()
    {
        var otherModules = ModuleAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("Decisya.Modules.", StringComparison.Ordinal))
            .Where(n => !n.EndsWith(".Contracts", StringComparison.Ordinal))
            .Where(n => n != ModuleAssembly.GetName().Name);

        otherModules.Should().BeEmpty();
    }

    [Fact]
    public void The_Admin_module_never_reads_the_system_clock_directly()
    {
        var result = Types.InAssembly(ModuleAssembly)
            .Should()
            .NotHaveDependencyOn("NodaTime.SystemClock")
            .GetResult();

        result.IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public void Among_the_Decisya_assemblies_the_Admin_module_references_exactly_its_Contracts_Entitlements_Contracts_and_SharedKernel()
    {
        ModuleAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("Decisya.", StringComparison.Ordinal))
            .Should().BeEquivalentTo(["Decisya.Modules.Admin.Contracts", "Decisya.Modules.Entitlements.Contracts", "Decisya.SharedKernel"]);
    }

    [Fact]
    public void The_Admin_module_references_no_persistence_no_EF_Core_no_Npgsql_no_Audit_and_no_Tenancy_assembly()
    {
        var referenced = ModuleAssembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToList();

        referenced.Should().NotContain(n => n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        referenced.Should().NotContain(n => n.StartsWith("Npgsql", StringComparison.Ordinal));
        referenced.Should().NotContain("Decisya.Infrastructure.Persistence");
        referenced.Should().NotContain("Decisya.Modules.Audit.Contracts");
        referenced.Should().NotContain("Decisya.Modules.Tenancy.Contracts");
        referenced.Should().NotContain("Decisya.Modules.Entitlements");
    }

    [Fact]
    public void No_Admin_type_depends_on_DbConnection_or_DbTransaction()
    {
        var result = Types.InAssembly(ModuleAssembly)
            .Should()
            .NotHaveDependencyOnAny("System.Data.Common.DbConnection", "System.Data.Common.DbTransaction", "System.Data.Common.DbCommand")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void The_Admin_module_persists_nothing_it_has_no_DbContext_no_Domain_and_no_Infrastructure_namespace()
    {
        ModuleTypes().Select(t => t.Namespace).Should().NotContain(ns =>
            ns != null && (ns.EndsWith(".Domain", StringComparison.Ordinal) || ns.EndsWith(".Infrastructure", StringComparison.Ordinal)
                || ns.Contains(".Domain.", StringComparison.Ordinal) || ns.Contains(".Infrastructure.", StringComparison.Ordinal)));
        ModuleTypes().Select(t => t.Name).Should().NotContain(n => n.EndsWith("DbContext", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_Endpoints_and_AdminModule_depend_on_AspNetCore()
    {
        var result = Types.InAssembly(ModuleAssembly)
            .That()
            .DoNotResideInNamespace("Decisya.Modules.Admin.Endpoints")
            .And()
            .DoNotHaveName(nameof(AdminModule))
            .ShouldNot()
            .HaveDependencyOn("Microsoft.AspNetCore")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void InternalsVisibleTo_is_exactly_the_module_test_assembly()
    {
        ModuleAssembly.GetCustomAttributes<InternalsVisibleToAttribute>().Select(a => a.AssemblyName)
            .Should().Equal("Decisya.Modules.Admin.Tests");
    }

    [Fact]
    public void The_Admin_module_declares_no_AllowCrossTenant_type()
    {
        ModuleAssembly.GetTypes().Where(t => t.GetCustomAttribute<AllowCrossTenantAttribute>() is not null).Should().BeEmpty();
    }

    [Fact]
    public void No_Admin_member_or_parameter_is_a_DateTime_DateTimeOffset_or_TimeZoneInfo()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        Type[] banned = [typeof(DateTime), typeof(DateTimeOffset), typeof(DateTime?), typeof(DateTimeOffset?), typeof(TimeZoneInfo)];

        foreach (var type in ModuleTypes().Concat(ContractsAssembly.GetTypes()))
        {
            type.GetProperties(all).Select(p => p.PropertyType).Should().NotContain(banned, $"{type.Name} properties");
            type.GetFields(all).Select(f => f.FieldType).Should().NotContain(banned, $"{type.Name} fields");
            foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                method.GetParameters().Select(p => p.ParameterType).Should().NotContain(banned, $"{type.Name}.{method.Name}");
            }
        }
    }

    [Fact]
    public void The_endpoints_call_only_IEntitlementAdminCommands_among_the_other_modules()
    {
        // Every dependency on another module is on the Entitlements.Contracts.Admin facade or on the
        // shared Contracts types its signatures carry (FeatureKey); never on a handler, a command or a context.
        var offenders = Types.InAssembly(ModuleAssembly)
            .That().ResideInNamespace("Decisya.Modules.Admin.Endpoints")
            .ShouldNot().HaveDependencyOnAny(
                "Decisya.Modules.Entitlements.Application",
                "Decisya.Modules.Entitlements.Domain",
                "Decisya.Modules.Entitlements.Infrastructure",
                "Decisya.Modules.Audit",
                "Decisya.Modules.Tenancy",
                "Decisya.Infrastructure")
            .GetResult();
        offenders.IsSuccessful.Should().BeTrue(string.Join(", ", offenders.FailingTypeNames ?? []));

        // Positive: the endpoints do use the facade, so the rule above is not vacuous.
        Types.InAssembly(ModuleAssembly)
            .That().ResideInNamespace("Decisya.Modules.Admin.Endpoints")
            .And().HaveDependencyOn(typeof(IEntitlementAdminCommands).FullName!)
            .GetTypes().Should().NotBeEmpty();

        // The endpoint handlers' parameters carry the facade and no other module service.
        var endpointType = ModuleAssembly.GetType("Decisya.Modules.Admin.Endpoints.AdminEndpoints", throwOnError: true)!;
        var serviceParameters = endpointType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(m => m.Name.EndsWith("Async", StringComparison.Ordinal))
            .SelectMany(m => m.GetParameters())
            .Select(p => p.ParameterType)
            .Where(t => t.Namespace != null && t.Namespace.StartsWith("Decisya.", StringComparison.Ordinal))
            .Where(t => t != typeof(TenantId))
            .Distinct();
        serviceParameters.Should().OnlyContain(t => t == typeof(IEntitlementAdminCommands) || t.Name == "AdminRequests");
    }

    [Fact]
    public void No_Admin_type_declares_a_database_transaction_or_audit_write_of_its_own()
    {
        var result = Types.InAssembly(ModuleAssembly)
            .ShouldNot().HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Npgsql", "Decisya.Modules.Audit.Contracts.IAuditWriter")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void The_request_DTO_and_the_Contracts_assembly_reference_only_SharedKernel()
    {
        ContractsAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("Decisya.", StringComparison.Ordinal))
            .Should().BeEquivalentTo(["Decisya.SharedKernel"]);
    }

    [Fact]
    public void OverrideGrantRequest_ToString_never_renders_the_reason()
    {
        var marker = $"MARKER-{Guid.NewGuid():N}";
        var request = new OverrideGrantRequest(marker, "2030-01-01T00:00:00Z");

        request.ToString().Should().NotContain(marker).And.NotContain("2030");
        $"{request}".Should().NotContain(marker);
    }

    [Fact]
    public void The_strict_serializer_options_disallow_unknown_members_and_duplicates_and_cap_the_depth()
    {
        var options = Endpoints.AdminJson.Options;

        options.UnmappedMemberHandling.Should().Be(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow);
        options.AllowDuplicateProperties.Should().BeFalse();
        options.MaxDepth.Should().Be(4);
    }
}
