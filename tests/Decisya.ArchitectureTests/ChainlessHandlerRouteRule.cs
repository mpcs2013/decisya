using System.Reflection;
using Mono.Cecil;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #231, R17 (G3 S-12; ADR-0024 amendment 2 item 3). WolverineFx 6.39.1 routes four message types
/// to hand-written handlers that are not generated chains and so run no envelope tenant middleware:
/// <c>Wolverine.Envelope</c>, <c>Wolverine.Runtime.Agents.IAgentCommand</c> (any implementing type, by
/// interface), <c>Wolverine.Runtime.RemoteInvocation.Acknowledgement</c> and
/// <c>FailureAcknowledgement</c>. The chain walk pins those four; it cannot list the implementers of
/// <c>IAgentCommand</c>. This rule closes the routes: no type in the scope implements
/// <c>IAgentCommand</c> (directly, through a base type or through another interface), and none creates
/// one of the other three. Exact full names, never a namespace or an assembly; compiler-generated types
/// count as their declaring type; the exemption list is exact type names and empty in #231.
/// </summary>
/// <remarks>
/// "Creates" is any reference to a constructor of the target, so a type that derives from
/// <c>Envelope</c> (a base-constructor call) fails too: an over-approximation, never an under-one.
/// </remarks>
public static class ChainlessHandlerRouteRule
{
    internal const string AgentCommand = "Wolverine.Runtime.Agents.IAgentCommand";

    internal static readonly IReadOnlyList<string> CreatedTargets =
    [
        "Wolverine.Envelope",
        "Wolverine.Runtime.RemoteInvocation.Acknowledgement",
        "Wolverine.Runtime.RemoteInvocation.FailureAcknowledgement",
    ];

    /// <summary>Exact type full names (reflection style) allowed to route into a chain-less handler. Empty in #231.</summary>
    internal static readonly IReadOnlySet<string> Exemptions = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>One line per violation, naming the type and the target. Empty when the assemblies are clean.</summary>
    public static IReadOnlyList<string> Evaluate(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var failures = new List<string>();
        foreach (var assembly in assemblies)
        {
            using var definition = AssemblyDefinition.ReadAssembly(assembly.Location);

            foreach (var type in definition.MainModule.GetTypes().Where(t => !CompilerGeneratedTypeWalk.IsCompilerGenerated(t)))
            {
                var typeName = MethodReferenceScan.ReflectionName(type);
                if (Exemptions.Contains(typeName))
                {
                    continue;
                }

                if (Implements(type, AgentCommand))
                {
                    failures.Add($"{typeName} implements {AgentCommand}");
                }

                foreach (var created in MethodReferenceScan.ReferencesOf(type)
                    .Where(reference => reference.Name == ".ctor" && CreatedTargets.Contains(reference.DeclaringType.FullName))
                    .Select(reference => reference.DeclaringType.FullName)
                    .Distinct())
                {
                    failures.Add($"{typeName} creates {created}");
                }
            }
        }

        return failures;
    }

    private static bool Implements(TypeDefinition type, string interfaceName)
    {
        for (var current = type; current is not null; current = current.BaseType?.Resolve())
        {
            foreach (var implemented in current.Interfaces)
            {
                if (implemented.InterfaceType.FullName == interfaceName
                    || (implemented.InterfaceType.Resolve() is { } resolved && Implements(resolved, interfaceName)))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
