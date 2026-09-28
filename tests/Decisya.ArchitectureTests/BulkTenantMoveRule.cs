using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using NetArchTest.Rules;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #22, G4-22-05 (T-08). <c>ExecuteUpdate</c>/<c>ExecuteUpdateAsync</c>'s
/// <c>Action&lt;UpdateSettersBuilder&lt;T&gt;&gt;</c> setter delegate builds an expression tree
/// for each property selector, so a <c>SetProperty(p =&gt; p.TenantId, ...)</c> call compiles
/// to a method body containing both a <c>call</c> to <c>UpdateSettersBuilder&lt;T&gt;.SetProperty</c>
/// and an <c>ldtoken</c> referencing a <c>get_TenantId</c> accessor. That method fails this
/// rule, unconditionally — <c>[AllowCrossTenant]</c> does not exempt it, because moving a row
/// between tenants is never allowed, not even for an audited admin handler.
/// </summary>
public static class BulkTenantMoveRule
{
    public static NetArchTest.Rules.TestResult Evaluate(params Assembly[] assemblies) =>
        Types.InAssemblies(assemblies)
            .Should()
            .MeetCustomRule(new NoMethodSetsTenantIdInBulk())
            .GetResult();

    private sealed class NoMethodSetsTenantIdInBulk : ICustomRule
    {
        public bool MeetsRule(TypeDefinition type) =>
            !CompilerGeneratedTypeWalk.MethodsIncludingCompilerGeneratedDescendants(type)
                .Any(m => m.Method.HasBody && ViolatesRule(m.Method.Body));

        private static bool ViolatesRule(Mono.Cecil.Cil.MethodBody body)
        {
            var callsSetProperty = false;
            var referencesTenantIdGetter = false;

            foreach (var instruction in body.Instructions)
            {
                if (IsSetPropertyCall(instruction))
                {
                    callsSetProperty = true;
                }
                else if (IsTenantIdGetterToken(instruction))
                {
                    referencesTenantIdGetter = true;
                }
            }

            return callsSetProperty && referencesTenantIdGetter;
        }

        private static bool IsSetPropertyCall(Instruction instruction)
        {
            if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
            {
                return false;
            }

            var methodReference = AsMethodReference(instruction.Operand);

            return methodReference is not null &&
                methodReference.Name == "SetProperty" &&
                methodReference.DeclaringType.Name == "UpdateSettersBuilder`1";
        }

        private static bool IsTenantIdGetterToken(Instruction instruction)
        {
            if (instruction.OpCode != OpCodes.Ldtoken)
            {
                return false;
            }

            var methodReference = AsMethodReference(instruction.Operand);

            // Matched by accessor name only (not by resolving the declaring type's interfaces):
            // every ITenantScoped implementation names this accessor get_TenantId, by contract
            // (Decisya.SharedKernel.Tenancy.ITenantScoped.TenantId), and this rule has no
            // AllowCrossTenant escape hatch to fall back on if a resolution across assemblies
            // ever failed.
            return methodReference is not null && methodReference.Name == "get_TenantId";
        }

        private static MethodReference? AsMethodReference(object? operand) => operand switch
        {
            GenericInstanceMethod generic => generic.ElementMethod,
            MethodReference method => method,
            _ => null,
        };
    }
}
