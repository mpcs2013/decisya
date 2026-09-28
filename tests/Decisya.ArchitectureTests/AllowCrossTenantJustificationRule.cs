using System.Reflection;
using Decisya.SharedKernel.Tenancy;
using Mono.Cecil;
using NetArchTest.Rules;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #22, Story 6 scenario 3. Every type carrying <see cref="AllowCrossTenantAttribute"/>
/// must supply a non-blank justification. The constructor argument is read from Mono Cecil's
/// <c>CustomAttribute.ConstructorArguments</c> directly, never by constructing the
/// attribute: <see cref="AllowCrossTenantAttribute"/>'s constructor never throws precisely so
/// that reflection over the whole assembly (this rule, and #24's future audit-log discovery)
/// never breaks on a blank justification.
/// </summary>
public static class AllowCrossTenantJustificationRule
{
    public static NetArchTest.Rules.TestResult Evaluate(params Assembly[] assemblies) =>
        Types.InAssemblies(assemblies)
            .That()
            .HaveCustomAttribute(typeof(AllowCrossTenantAttribute))
            .Should()
            .MeetCustomRule(new HasNonBlankJustification())
            .GetResult();

    private sealed class HasNonBlankJustification : ICustomRule
    {
        public bool MeetsRule(TypeDefinition type)
        {
            var attribute = type.CustomAttributes.FirstOrDefault(a =>
                a.AttributeType.FullName == typeof(AllowCrossTenantAttribute).FullName);

            if (attribute is null || attribute.ConstructorArguments.Count == 0)
            {
                return false;
            }

            var justification = attribute.ConstructorArguments[0].Value as string;

            return !string.IsNullOrWhiteSpace(justification);
        }
    }
}
