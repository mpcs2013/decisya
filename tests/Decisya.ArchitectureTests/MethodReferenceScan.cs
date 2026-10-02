using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Shared IL scan for the #24 rules (<see cref="CrossTenantAuditRule"/>,
/// <see cref="ExplicitTransactionRule"/>, <see cref="RawAdoNetRule"/>): every method a type
/// references, by <c>call</c>, <c>callvirt</c>, <c>newobj</c>, <c>ldftn</c>, <c>ldvirtftn</c> or
/// <c>ldtoken</c> (so method groups and expression trees count), including the bodies of the
/// type's compiler-generated closures and async state machines (see <see cref="CompilerGeneratedTypeWalk"/>).
/// Same reference model as <see cref="CrossTenantQueryRule"/>.
/// </summary>
internal static class MethodReferenceScan
{
    private static readonly OpCode[] MethodReferencingOpCodes =
    [
        OpCodes.Call, OpCodes.Callvirt, OpCodes.Newobj, OpCodes.Ldftn, OpCodes.Ldvirtftn, OpCodes.Ldtoken,
    ];

    public static IEnumerable<MethodReference> ReferencesOf(TypeDefinition type)
    {
        foreach (var (_, method) in CompilerGeneratedTypeWalk.MethodsIncludingCompilerGeneratedDescendants(type))
        {
            if (!method.HasBody)
            {
                continue;
            }

            foreach (var instruction in method.Body.Instructions)
            {
                if (!MethodReferencingOpCodes.Contains(instruction.OpCode))
                {
                    continue;
                }

                var reference = instruction.Operand switch
                {
                    GenericInstanceMethod generic => generic.ElementMethod,
                    MethodReference direct => direct,
                    _ => null,
                };

                if (reference is not null)
                {
                    yield return reference;
                }
            }
        }
    }

    /// <summary>The type's full name in .NET reflection style (<c>Outer+Inner</c>), as an allow-list names it.</summary>
    public static string ReflectionName(TypeDefinition type) => CompilerGeneratedTypeWalk.ReflectionStyleFullName(type);
}
