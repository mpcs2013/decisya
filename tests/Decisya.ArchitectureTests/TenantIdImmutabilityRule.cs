using System.Reflection;
using Decisya.SharedKernel.Tenancy;
using Mono.Cecil;
using NetArchTest.Rules;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #22, Story 1. Every type that implements <see cref="ITenantScoped"/> must declare its
/// <c>TenantId</c> property with no set accessor of any visibility, <c>init</c> included: EF
/// Core writes the compiler-generated backing field directly on materialization (see
/// <see cref="ITenantScoped"/>'s remarks), so a set accessor is never needed, and one would let
/// a later change reassign a persisted entity's tenant.
/// </summary>
public static class TenantIdImmutabilityRule
{
    public static NetArchTest.Rules.TestResult Evaluate(params Assembly[] assemblies) =>
        Types.InAssemblies(assemblies)
            .That()
            .ImplementInterface(typeof(ITenantScoped))
            .Should()
            .MeetCustomRule(new DeclaresNoTenantIdSetter())
            .GetResult();

    private sealed class DeclaresNoTenantIdSetter : ICustomRule
    {
        public bool MeetsRule(TypeDefinition type)
        {
            var property = type.Properties.FirstOrDefault(p => p.Name == nameof(ITenantScoped.TenantId));

            return property is null || property.SetMethod is null;
        }
    }
}
