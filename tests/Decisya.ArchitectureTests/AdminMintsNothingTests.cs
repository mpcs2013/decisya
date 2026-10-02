using Decisya.Modules.Admin;
using Decisya.SharedKernel.Tenancy;
using Mono.Cecil;
using NetArchTest.Rules;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #25, G2 rules table: "Admin mints nothing and is not cross-tenant: no [AllowCrossTenant]
/// type, and no reference to TenantResolution.For, FromClaim or get_NoTenant (explicit, in addition
/// to CrossTenantQueryRule)". The endpoints only hand a parsed tenant id to the Entitlements facade.
/// IL scan through <see cref="MethodReferenceScan"/>, so async state machines and closures count.
/// </summary>
public class AdminMintsNothingTests
{
    private static readonly (string DeclaringType, string MethodName)[] Minting =
    [
        ("Decisya.SharedKernel.Tenancy.TenantResolution", "For"),
        ("Decisya.SharedKernel.Tenancy.TenantResolution", "FromClaim"),
        ("Decisya.SharedKernel.Tenancy.TenantResolution", "get_NoTenant"),
    ];

    [Fact]
    public void No_Admin_type_carries_AllowCrossTenant()
    {
        var attributed = typeof(AdminModule).Assembly.GetTypes()
            .Where(t => Attribute.IsDefined(t, typeof(AllowCrossTenantAttribute), inherit: false))
            .Select(t => t.FullName);

        attributed.Should().BeEmpty();
    }

    [Fact]
    public void No_Admin_type_references_TenantResolution_For_FromClaim_or_NoTenant()
    {
        var offences = new List<string>();
        var seen = new List<string>();

        var result = Types.InAssembly(typeof(AdminModule).Assembly)
            .Should()
            .MeetCustomRule(new Scan(offences, seen))
            .GetResult();

        result.IsSuccessful.Should().BeTrue();
        seen.Should().NotBeEmpty("the scan must visit the Admin types, or the rule proves nothing");
        offences.Should().BeEmpty("the Admin module mints no tenant scope and bypasses no filter");
    }

    private sealed class Scan(List<string> offences, List<string> seen) : ICustomRule
    {
        public bool MeetsRule(TypeDefinition type)
        {
            if (CompilerGeneratedTypeWalk.IsCompilerGenerated(type))
            {
                // Credited to the enclosing type by MethodReferenceScan.
                return true;
            }

            seen.Add(MethodReferenceScan.ReflectionName(type));

            foreach (var reference in MethodReferenceScan.ReferencesOf(type))
            {
                if (Minting.Contains((reference.DeclaringType.FullName, reference.Name)))
                {
                    offences.Add($"{MethodReferenceScan.ReflectionName(type)} -> {reference.DeclaringType.Name}.{reference.Name}");
                }
            }

            return true;
        }
    }
}
