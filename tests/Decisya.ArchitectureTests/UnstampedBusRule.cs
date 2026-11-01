using System.Reflection;
using Mono.Cecil;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #231, R14 (ADR-0024 amendment 1 item 3; G3 S-09). The only stamped bus is the scope's
/// <c>IMessageBus</c> (<c>EnvelopeTenantStamp</c>). WolverineFx 6.39.1 also keeps resolvable, and unstamped,
/// <c>Wolverine.IMessageContext</c>, <c>Wolverine.Runtime.MessageContext</c> and
/// <c>Wolverine.Runtime.MessageBus</c>, and an extension that builds a bus from a host
/// (<c>host.MessageBus()</c>). No type in the scope references any of the four.
/// </summary>
/// <remarks>
/// <para>
/// A reference is any use: a signature, field, property, event, local, base type, interface, generic
/// argument, cast, <c>typeof</c>, object creation or member call. Compiler-generated types count as their
/// declaring type.
/// </para>
/// <para>
/// The exemption (G3 S-09) is a list of exact type full names in the exact assembly <c>Decisya.Api</c>, and
/// it is <b>empty</b> in #231: not the namespace <c>Decisya.Api.Messaging</c>, which G2 first proposed.
/// The first handler with pre-generated code adds the generated namespace
/// <c>Internal.Generated.WolverineHandlers</c> under its own G2 (ADR-0024 amendment 1 item 3). A same-named
/// type in another assembly is not exempt.
/// </para>
/// </remarks>
public static class UnstampedBusRule
{
    internal const string ExemptAssembly = "Decisya.Api";
    internal const string HostCallTarget = "host.MessageBus()";

    private const string HostType = "Microsoft.Extensions.Hosting.IHost";
    private const string HostBusMethod = "MessageBus";

    /// <summary>Exact type full names (reflection style) in <c>Decisya.Api</c> allowed to reference an unstamped bus. Empty in #231.</summary>
    internal static readonly IReadOnlySet<string> Exemptions = new HashSet<string>(StringComparer.Ordinal);

    internal static readonly IReadOnlySet<string> BannedTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Wolverine.IMessageContext",
        "Wolverine.Runtime.MessageContext",
        "Wolverine.Runtime.MessageBus",
    };

    /// <summary>One line per violation, naming the type and the target. Empty when the assemblies are clean.</summary>
    public static IReadOnlyList<string> Evaluate(IEnumerable<Assembly> assemblies) =>
        Evaluate(assemblies, Exemptions, ExemptAssembly);

    internal static IReadOnlyList<string> Evaluate(
        IEnumerable<Assembly> assemblies, IReadOnlySet<string> exemptions, string exemptAssembly)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        ArgumentNullException.ThrowIfNull(exemptions);

        var failures = new List<string>();
        foreach (var assembly in assemblies)
        {
            using var definition = AssemblyDefinition.ReadAssembly(assembly.Location);
            var exemptHere = string.Equals(definition.Name.Name, exemptAssembly, StringComparison.Ordinal);

            foreach (var type in definition.MainModule.GetTypes().Where(t => !CompilerGeneratedTypeWalk.IsCompilerGenerated(t)))
            {
                var typeName = MethodReferenceScan.ReflectionName(type);
                if (exemptHere && exemptions.Contains(typeName))
                {
                    continue;
                }

                foreach (var target in BannedReferencesIn(type).Distinct())
                {
                    failures.Add($"{typeName} references {target}");
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

        return [.. Exemptions.Where(name => !known.Contains(name))];
    }

    private static IEnumerable<string> BannedReferencesIn(TypeDefinition type)
    {
        foreach (var reference in TypeReferencesOf(type))
        {
            foreach (var flat in IlScan.Flatten(reference))
            {
                if (BannedTypes.Contains(flat.FullName))
                {
                    yield return flat.FullName;
                }
            }
        }

        foreach (var method in MethodReferenceScan.ReferencesOf(type))
        {
            if (string.Equals(method.Name, HostBusMethod, StringComparison.Ordinal)
                && method.Parameters.Count == 1
                && string.Equals(method.Parameters[0].ParameterType.FullName, HostType, StringComparison.Ordinal)
                && IlScan.IsWolverineFamily(IlScan.AssemblyNameOf(IlScan.ElementOf(method.DeclaringType))))
            {
                yield return HostCallTarget;
            }
        }
    }

    private static IEnumerable<TypeReference> TypeReferencesOf(TypeDefinition type)
    {
        foreach (var declared in IlScan.TypeAndGeneratedDescendants(type))
        {
            if (declared.BaseType is not null)
            {
                yield return declared.BaseType;
            }

            foreach (var implemented in declared.Interfaces)
            {
                yield return implemented.InterfaceType;
            }

            foreach (var field in declared.Fields)
            {
                yield return field.FieldType;
            }

            foreach (var property in declared.Properties)
            {
                yield return property.PropertyType;
            }

            foreach (var declaredEvent in declared.Events)
            {
                yield return declaredEvent.EventType;
            }

            foreach (var method in declared.Methods)
            {
                yield return method.ReturnType;

                foreach (var parameter in method.Parameters)
                {
                    yield return parameter.ParameterType;
                }

                if (!method.HasBody)
                {
                    continue;
                }

                foreach (var variable in method.Body.Variables)
                {
                    yield return variable.VariableType;
                }

                foreach (var handler in method.Body.ExceptionHandlers)
                {
                    if (handler.CatchType is not null)
                    {
                        yield return handler.CatchType;
                    }
                }

                foreach (var instruction in method.Body.Instructions)
                {
                    foreach (var operandType in TypesNamedBy(instruction.Operand))
                    {
                        yield return operandType;
                    }
                }
            }
        }
    }

    private static IEnumerable<TypeReference> TypesNamedBy(object? operand)
    {
        switch (operand)
        {
            case TypeReference typeOperand:
                yield return typeOperand;
                yield break;
            case GenericInstanceMethod generic:
                yield return generic.ElementMethod.DeclaringType;
                yield return generic.ElementMethod.ReturnType;
                foreach (var parameter in generic.ElementMethod.Parameters)
                {
                    yield return parameter.ParameterType;
                }

                foreach (var argument in generic.GenericArguments)
                {
                    yield return argument;
                }

                yield break;
            case MethodReference method:
                yield return method.DeclaringType;
                yield return method.ReturnType;
                foreach (var parameter in method.Parameters)
                {
                    yield return parameter.ParameterType;
                }

                yield break;
            case FieldReference field:
                yield return field.DeclaringType;
                yield return field.FieldType;
                yield break;
            default:
                yield break;
        }
    }
}
