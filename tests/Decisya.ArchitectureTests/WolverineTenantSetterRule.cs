using System.Reflection;
using Mono.Cecil;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #231, R6 (ADR-0024 point 4; G3 G4-231-01, T-04). The envelope tenant has one writer:
/// <c>Decisya.Api.Messaging.EnvelopeTenantStamp</c>. A tenant crosses the bus on the envelope, so any
/// Wolverine member that takes or sets a tenant could forge another tenant's scope. The rule therefore
/// matches by member name, not by a short list: every member of a type in a <c>Wolverine</c> or
/// <c>JasperFx*</c> assembly whose name contains <c>Tenant</c> (case-insensitive: <c>set_TenantId</c>,
/// <c>ForTenant</c>, <c>InvokeForTenantAsync</c> and the like) is referenced only by the stamp.
/// </summary>
/// <remarks>
/// <para>
/// The one exception is the exact getter <c>get_TenantId</c>, which the receive middleware
/// (<c>Decisya.Api.Messaging.EnvelopeTenantMiddleware</c>) reads and the stamp may read. Both exemptions
/// are exact type full names in the exact assembly <c>Decisya.Api</c>: never a namespace, a prefix or a
/// same-named type in another assembly. Compiler-generated types count as their declaring type, and
/// method, field and <c>ldtoken</c> references all count.
/// </para>
/// <para>
/// The scan reads names only; it does not follow a call into a helper. A Wolverine member that does not
/// contain <c>Tenant</c> but still moves a tenant would be missed: the behaviour tests of the stamp and
/// the receive middleware (<c>Decisya.Api.Tests</c>) are the backstop.
/// </para>
/// </remarks>
public static class WolverineTenantSetterRule
{
    internal const string StampType = "Decisya.Api.Messaging.EnvelopeTenantStamp";
    internal const string MiddlewareType = "Decisya.Api.Messaging.EnvelopeTenantMiddleware";
    internal const string ExemptAssembly = "Decisya.Api";

    private const string TenantGetter = "get_TenantId";

    /// <summary>The exact types allowed to reference any Wolverine tenant member.</summary>
    internal static readonly IReadOnlySet<string> Writers = new HashSet<string>(StringComparer.Ordinal) { StampType };

    /// <summary>The exact types allowed to reference the <c>get_TenantId</c> getter (and nothing else tenant-named).</summary>
    internal static readonly IReadOnlySet<string> GetterReaders =
        new HashSet<string>(StringComparer.Ordinal) { StampType, MiddlewareType };

    /// <summary>One line per violation, naming the type and the member. Empty when the assemblies are clean.</summary>
    public static IReadOnlyList<string> Evaluate(IEnumerable<Assembly> assemblies) =>
        Evaluate(assemblies, Writers, GetterReaders, ExemptAssembly);

    internal static IReadOnlyList<string> Evaluate(
        IEnumerable<Assembly> assemblies,
        IReadOnlySet<string> writers,
        IReadOnlySet<string> getterReaders,
        string exemptAssembly)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        ArgumentNullException.ThrowIfNull(writers);
        ArgumentNullException.ThrowIfNull(getterReaders);

        var failures = new List<string>();
        foreach (var assembly in assemblies)
        {
            using var definition = AssemblyDefinition.ReadAssembly(assembly.Location);
            var exemptHere = string.Equals(definition.Name.Name, exemptAssembly, StringComparison.Ordinal);

            foreach (var type in definition.MainModule.GetTypes().Where(t => !CompilerGeneratedTypeWalk.IsCompilerGenerated(t)))
            {
                var typeName = MethodReferenceScan.ReflectionName(type);

                foreach (var (declaring, member) in TenantMembersReferencedBy(type).Distinct())
                {
                    var exempt = exemptHere
                        && (writers.Contains(typeName)
                            || (string.Equals(member, TenantGetter, StringComparison.Ordinal) && getterReaders.Contains(typeName)));
                    if (!exempt)
                    {
                        failures.Add($"{typeName} references {declaring}.{member}");
                    }
                }
            }
        }

        return failures;
    }

    /// <summary>Every exemption must name an existing type in the given assemblies; an unknown name fails.</summary>
    public static IReadOnlyList<string> UnknownExemptions(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var known = assemblies
            .SelectMany(a => a.GetTypes())
            .Select(t => t.FullName)
            .ToHashSet(StringComparer.Ordinal);

        return [.. Writers.Concat(GetterReaders).Distinct(StringComparer.Ordinal).Where(name => !known.Contains(name))];
    }

    private static IEnumerable<(string Declaring, string Member)> TenantMembersReferencedBy(TypeDefinition type)
    {
        foreach (var (_, method) in CompilerGeneratedTypeWalk.MethodsIncludingCompilerGeneratedDescendants(type))
        {
            if (!method.HasBody)
            {
                continue;
            }

            foreach (var instruction in method.Body.Instructions)
            {
                if (!IlScan.TryMemberOperand(instruction.Operand, out var declaringType, out var member))
                {
                    continue;
                }

                var declaring = IlScan.ElementOf(declaringType);
                if (IlScan.IsWolverineFamily(IlScan.AssemblyNameOf(declaring))
                    && member.Contains("Tenant", StringComparison.OrdinalIgnoreCase))
                {
                    yield return (declaring.FullName, member);
                }
            }
        }
    }
}
