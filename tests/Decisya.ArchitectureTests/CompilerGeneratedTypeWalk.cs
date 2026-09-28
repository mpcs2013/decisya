using Mono.Cecil;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Shared IL-analysis helpers for <see cref="CrossTenantQueryRule"/> and
/// <see cref="BulkTenantMoveRule"/> (issue #22, G4-22-04): crediting a reference found inside
/// a compiler-generated closure or async state machine to the nearest user-declared ancestor
/// type, and formatting a Mono.Cecil nested-type name the way .NET reflection would
/// (<c>Outer+Inner</c>, not Cecil's own <c>Outer/Inner</c>).
/// </summary>
internal static class CompilerGeneratedTypeWalk
{
    /// <summary>
    /// <see langword="true"/> for a type the C# compiler generated for a closure
    /// (<c>&lt;&gt;c</c>, <c>&lt;&gt;c__DisplayClassN_M</c>) or an async/iterator state machine
    /// (<c>&lt;MethodName&gt;d__N</c>): it carries <see cref="System.Runtime.CompilerServices.CompilerGeneratedAttribute"/>,
    /// or its name starts with <c>&lt;</c> (compiler-generated type names are never valid C#
    /// identifiers).
    /// </summary>
    public static bool IsCompilerGenerated(TypeDefinition type) =>
        type.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute") ||
        type.Name.StartsWith('<');

    /// <summary>
    /// Walks from <paramref name="type"/> up through <c>TypeDefinition.DeclaringType</c>
    /// while the current type is compiler-generated, stopping at the first type that isn't —
    /// the type a human actually declared, and the one <c>[AllowCrossTenant]</c> must be found
    /// on. A user-declared nested type (not compiler-generated) is its own credited type: the
    /// walk never continues past it, so it never inherits an outer type's attribute.
    /// </summary>
    public static TypeDefinition CreditedType(TypeDefinition type)
    {
        var current = type;

        while (IsCompilerGenerated(current) && current.DeclaringType is not null)
        {
            current = current.DeclaringType;
        }

        return current;
    }

    /// <summary>
    /// <paramref name="type"/>'s full name with every Cecil nested-type separator (<c>/</c>)
    /// rewritten to the .NET reflection convention (<c>+</c>), e.g. <c>Outer+Helper</c>.
    /// </summary>
    public static string ReflectionStyleFullName(TypeDefinition type) => type.FullName.Replace('/', '+');

    /// <summary>
    /// <paramref name="type"/>'s own methods, plus (recursively) the methods of every nested
    /// type that is compiler-generated. NetArchTest's own type enumeration never visits a
    /// compiler-generated nested type directly (a closure or an async state machine is never
    /// itself selected by <c>Types.InAssembly(...)</c>), so a rule that only inspected
    /// <c>type.Methods</c> would miss a reference that exists only inside a lambda or an
    /// <c>await</c>ed method body — exactly where <c>ExecuteUpdate(s =&gt; s.SetProperty(...))</c>
    /// and an <c>async</c> conflict handler's body actually live. A user-declared nested type
    /// (for example <c>Outer.Helper</c>) is deliberately not recursed into here: NetArchTest
    /// visits it independently, with its own, unrelated credited-type check.
    /// </summary>
    public static IEnumerable<(TypeDefinition ContainingType, MethodDefinition Method)> MethodsIncludingCompilerGeneratedDescendants(
        TypeDefinition type)
    {
        foreach (var method in type.Methods)
        {
            yield return (type, method);
        }

        foreach (var nested in type.NestedTypes)
        {
            if (!IsCompilerGenerated(nested))
            {
                continue;
            }

            foreach (var descendant in MethodsIncludingCompilerGeneratedDescendants(nested))
            {
                yield return descendant;
            }
        }
    }
}
