using Mono.Cecil;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Small Mono.Cecil helpers shared by the #231 rules (<see cref="WolverineTenantSetterRule"/>,
/// <see cref="UnstampedBusRule"/>, <see cref="TenantKeyRule"/>): which assembly a referenced type lives in,
/// the types hidden inside a generic instance or an array, and the members an instruction names.
/// </summary>
internal static class IlScan
{
    /// <summary>The method an operand names (a generic instance resolves to its element), or <see langword="null"/>.</summary>
    internal static MethodReference? MethodOperand(object? operand) => operand switch
    {
        GenericInstanceMethod generic => generic.ElementMethod,
        MethodReference direct => direct,
        _ => null,
    };

    /// <summary>
    /// The member (method or field) an operand names, with its declaring type. Used for <c>call</c>,
    /// <c>callvirt</c>, <c>newobj</c>, <c>ldftn</c>, <c>ldtoken</c>, <c>ldfld</c>, <c>stfld</c> and friends.
    /// </summary>
    internal static bool TryMemberOperand(object? operand, out TypeReference declaringType, out string name)
    {
        switch (operand)
        {
            case GenericInstanceMethod generic:
                declaringType = generic.ElementMethod.DeclaringType;
                name = generic.ElementMethod.Name;
                return true;
            case MethodReference method:
                declaringType = method.DeclaringType;
                name = method.Name;
                return true;
            case FieldReference field:
                declaringType = field.DeclaringType;
                name = field.Name;
                return true;
            default:
                declaringType = null!;
                name = string.Empty;
                return false;
        }
    }

    /// <summary>
    /// The type itself and, recursively, every type argument, array or by-ref element type it is built
    /// from. A generic parameter yields nothing.
    /// </summary>
    internal static IEnumerable<TypeReference> Flatten(TypeReference? type)
    {
        switch (type)
        {
            case null:
            case GenericParameter:
                yield break;
            case GenericInstanceType generic:
                foreach (var element in Flatten(generic.ElementType))
                {
                    yield return element;
                }

                foreach (var argument in generic.GenericArguments)
                {
                    foreach (var inner in Flatten(argument))
                    {
                        yield return inner;
                    }
                }

                yield break;
            case TypeSpecification specification:
                foreach (var element in Flatten(specification.ElementType))
                {
                    yield return element;
                }

                yield break;
            default:
                yield return type;
                yield break;
        }
    }

    /// <summary>A generic instance resolves to its generic type definition; anything else is returned as is.</summary>
    internal static TypeReference ElementOf(TypeReference type) =>
        type is GenericInstanceType generic ? generic.ElementType : type;

    /// <summary>The simple name of the assembly a referenced type is declared in, or <see langword="null"/>.</summary>
    internal static string? AssemblyNameOf(TypeReference type) => type.Scope switch
    {
        AssemblyNameReference assembly => assembly.Name,
        ModuleDefinition module => module.Assembly.Name.Name,
        _ => null,
    };

    /// <summary><see langword="true"/> for the <c>Wolverine</c> assemblies and the <c>JasperFx*</c> assemblies.</summary>
    internal static bool IsWolverineFamily(string? assemblyName) =>
        assemblyName is not null
        && (string.Equals(assemblyName, "Wolverine", StringComparison.Ordinal)
            || assemblyName.StartsWith("Wolverine.", StringComparison.Ordinal)
            || assemblyName.StartsWith("JasperFx", StringComparison.Ordinal));

    /// <summary>The type and, recursively, its compiler-generated nested types (closures, state machines).</summary>
    internal static IEnumerable<TypeDefinition> TypeAndGeneratedDescendants(TypeDefinition type)
    {
        yield return type;

        foreach (var nested in type.NestedTypes.Where(CompilerGeneratedTypeWalk.IsCompilerGenerated))
        {
            foreach (var descendant in TypeAndGeneratedDescendants(nested))
            {
                yield return descendant;
            }
        }
    }
}
