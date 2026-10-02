using Mono.Cecil;
using NetArchTest.Rules;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #24, G2 rules table row "The three Entitlements handlers and AuditWriter reference, from
/// <see cref="CrossTenantQueryRule.BypassList"/>, only TenantResolution.For". The [AllowCrossTenant]
/// attribute permits every bypass member, but these four types must use no raw SQL, no
/// <c>GetDbConnection</c>, no <c>IgnoreQueryFilters</c>, no <c>GetDatabaseValues</c>/<c>Reload</c>,
/// no <c>FromClaim</c> and no <c>NoTenant</c> minting. IL scan through <see cref="MethodReferenceScan"/>,
/// so async state machines and closures count.
/// </summary>
public class AuditBypassUsageTests
{
    private const string OnlyAllowedDeclaringType = "Decisya.SharedKernel.Tenancy.TenantResolution";
    private const string OnlyAllowedMethod = "For";

    private static readonly string[] CheckedTypes =
    [
        "Decisya.Modules.Entitlements.Application.StartTrialHandler",
        "Decisya.Modules.Entitlements.Application.GrantOverrideHandler",
        "Decisya.Modules.Entitlements.Application.RevokeOverrideHandler",
        "Decisya.Modules.Audit.Application.AuditWriter",
    ];

    private static List<(string Type, string Declaring, string Method)> BypassReferences()
    {
        var found = new List<(string, string, string)>();
        var seen = new HashSet<string>();

        var result = Types.InAssemblies(ArchitectureScope.Assemblies)
            .Should()
            .MeetCustomRule(new Collect(CheckedTypes, found, seen))
            .GetResult();

        result.IsSuccessful.Should().BeTrue();
        seen.Should().BeEquivalentTo(CheckedTypes, "every checked type must be found in the architecture scope, or the rule proves nothing");
        return found;
    }

    [Fact]
    public void The_three_Entitlements_handlers_and_AuditWriter_reference_only_TenantResolution_For_from_the_bypass_list()
    {
        var references = BypassReferences();

        references.Where(r => !(r.Declaring == OnlyAllowedDeclaringType && r.Method == OnlyAllowedMethod))
            .Should().BeEmpty("only TenantResolution.For may be used from the bypass list by these four types");
    }

    [Fact]
    public void The_scan_sees_TenantResolution_For_in_each_of_the_four_types_so_the_rule_is_not_vacuous()
    {
        var references = BypassReferences();

        foreach (var type in CheckedTypes)
        {
            references.Should().Contain(
                r => r.Type == type && r.Declaring == OnlyAllowedDeclaringType && r.Method == OnlyAllowedMethod, type);
        }
    }

    private sealed class Collect(
        string[] checkedTypes, List<(string Type, string Declaring, string Method)> found, HashSet<string> seen) : ICustomRule
    {
        public bool MeetsRule(TypeDefinition type)
        {
            var name = MethodReferenceScan.ReflectionName(type);
            if (!checkedTypes.Contains(name, StringComparer.Ordinal))
            {
                return true;
            }

            seen.Add(name);
            foreach (var reference in MethodReferenceScan.ReferencesOf(type))
            {
                if (CrossTenantQueryRule.BypassList.Contains((reference.DeclaringType.FullName, reference.Name)))
                {
                    found.Add((name, reference.DeclaringType.FullName, reference.Name));
                }
            }

            return true;
        }
    }
}
