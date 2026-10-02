using System.Reflection;
using Decisya.Modules.Admin;
using Decisya.Modules.Entitlements;
using NetArchTest.Rules;

namespace Decisya.ArchitectureTests;

/// <summary>
/// ADR-0012 amendment 1, point 5 (issue #25, G2 and G3 T-04): the cross-tenant admin surface
/// <c>Decisya.Modules.Entitlements.Contracts.Admin</c> is reachable only from
/// <c>Decisya.Modules.Admin</c> (the HTTP side) and <c>Decisya.Modules.Entitlements</c> (the
/// implementation). The rule runs over <see cref="ArchitectureScope"/>, every Contracts assembly,
/// <c>Decisya.Api</c> and <c>Decisya.ServiceDefaults</c>.
/// </summary>
public class AdminReachabilityTests
{
    private const string AdminContractsNamespace = "Decisya.Modules.Entitlements.Contracts.Admin";

    private static readonly string[] AllowedAssemblyNames = ["Decisya.Modules.Admin", "Decisya.Modules.Entitlements"];

    [Fact]
    public void Only_Admin_and_Entitlements_depend_on_the_Entitlements_admin_contracts_namespace()
    {
        var offenders = new List<string>();

        foreach (var assembly in AssembliesUnderRule())
        {
            var name = assembly.GetName().Name!;
            // The Contracts assembly declares the namespace itself; the two named assemblies use it.
            if (AllowedAssemblyNames.Contains(name, StringComparer.Ordinal)
                || string.Equals(name, "Decisya.Modules.Entitlements.Contracts", StringComparison.Ordinal))
            {
                continue;
            }

            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOn(AdminContractsNamespace).GetResult();
            if (!result.IsSuccessful)
            {
                offenders.AddRange((result.FailingTypeNames ?? []).Select(t => $"{name}: {t}"));
            }
        }

        offenders.Should().BeEmpty("only Modules.Admin and Modules.Entitlements may reach the cross-tenant admin contracts (ADR-0012 A1 point 5)");
    }

    [Fact]
    public void The_two_allowed_assemblies_do_depend_on_the_namespace_so_the_rule_is_not_vacuous()
    {
        foreach (var name in AllowedAssemblyNames)
        {
            var assembly = AssembliesUnderRule().Single(a => a.GetName().Name == name);

            var result = Types.InAssembly(assembly).That().HaveDependencyOn(AdminContractsNamespace).GetTypes();

            result.Should().NotBeEmpty($"{name} is expected to use {AdminContractsNamespace}");
        }
    }

    [Fact]
    public void The_scope_enumerates_Admin_Entitlements_Api_and_ServiceDefaults()
    {
        AssembliesUnderRule().Select(a => a.GetName().Name).Should().Contain(
            ["Decisya.Modules.Admin", "Decisya.Modules.Entitlements", "Decisya.Api", "Decisya.ServiceDefaults"]);
    }

    private static IEnumerable<Assembly> AssembliesUnderRule()
    {
        var assemblies = ArchitectureScope.Assemblies
            .Concat(ContractsScope.Assemblies)
            .Append(Assembly.Load("Decisya.Api"))
            .Append(Assembly.Load("Decisya.ServiceDefaults"));

        return assemblies.DistinctBy(a => a.GetName().Name, StringComparer.Ordinal);
    }

    // Anchors that keep the project references honest.
    private static readonly Type AdminAnchor = typeof(AdminModule);
    private static readonly Type EntitlementsAnchor = typeof(EntitlementsModule);
}
