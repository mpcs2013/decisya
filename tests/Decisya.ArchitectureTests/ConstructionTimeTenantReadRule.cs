using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #231, R16 (G3 G4-231-09; ADR-0024 amendment 2 item 1). WolverineFx 6.39.1 builds a handler's
/// scoped dependencies before the envelope tenant middleware has set the tenant, so a dependency that
/// reads the tenant or the caller when it is <i>constructed</i> sees <c>Invalid</c> (or throws, for
/// <c>UserId</c>) and freezes it. The approved pattern keeps the <c>ICurrentTenant</c> /
/// <c>ICurrentCaller</c> reference and reads it when it is used.
/// <para>
/// Banned: <c>ICurrentTenant.get_Resolution</c>, <c>ICurrentCaller.get_UserId</c> and
/// <c>get_IsPlatformAdmin</c>, every <c>RequestCaller</c> getter, and <c>IMessageBus.get_TenantId</c>,
/// matched by the referenced method's declaring type and exact name (a generic instance resolves to its
/// element). Checked: the own IL of every instance and static constructor (field initializers compile
/// into them), and the body of every <c>IServiceProvider</c> factory passed to a DI registration
/// (<c>ServiceCollectionServiceExtensions</c>, <c>ServiceCollectionDescriptorExtensions</c>,
/// <c>ServiceDescriptor</c>). A lambda or local function a constructor stores for later (for example
/// <c>new Lazy&lt;T&gt;(() =&gt; tenant.Resolution)</c>) compiles into a separate method and is not
/// read: that is the use-time pattern. An indirect read through a helper is not seen (accepted, Low:
/// a tripwire, backstopped by the behaviour proofs and the throwing <c>TenantDbContext</c>).
/// </para>
/// <para>
/// The factory detection over-approximates on purpose: any <c>ldftn</c> in a method that calls a
/// registration member, whose target takes an <c>IServiceProvider</c>, is a factory.
/// </para>
/// </summary>
public static class ConstructionTimeTenantReadRule
{
    /// <summary>Exact type full names (reflection style) allowed to read at construction time. Empty in #231.</summary>
    internal static readonly IReadOnlySet<string> Exemptions = new HashSet<string>(StringComparer.Ordinal);

    internal static readonly IReadOnlySet<(string DeclaringType, string Name)> BannedMembers = new HashSet<(string, string)>
    {
        ("Decisya.SharedKernel.Tenancy.ICurrentTenant", "get_Resolution"),
        ("Decisya.SharedKernel.Tenancy.ICurrentCaller", "get_UserId"),
        ("Decisya.SharedKernel.Tenancy.ICurrentCaller", "get_IsPlatformAdmin"),
        ("Decisya.Api.Authentication.RequestCaller", "get_Resolution"),
        ("Decisya.Api.Authentication.RequestCaller", "get_UserId"),
        ("Decisya.Api.Authentication.RequestCaller", "get_IsPlatformAdmin"),
        ("Decisya.Api.Authentication.RequestCaller", "get_HasMfaLevel"),
        ("Wolverine.IMessageBus", "get_TenantId"),
    };

    private static readonly string[] RegistrationTypes =
    [
        "Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions",
        "Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions",
        "Microsoft.Extensions.DependencyInjection.ServiceDescriptor",
    ];

    /// <summary>
    /// One line per violation: the type, the constructor or factory, and the banned member. Empty when
    /// the assemblies are clean.
    /// </summary>
    public static IReadOnlyList<string> Evaluate(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var failures = new List<string>();
        foreach (var assembly in assemblies)
        {
            using var definition = AssemblyDefinition.ReadAssembly(assembly.Location);

            foreach (var type in definition.MainModule.GetTypes())
            {
                var typeName = CompilerGeneratedTypeWalk.ReflectionStyleFullName(type);
                if (Exemptions.Contains(typeName))
                {
                    continue;
                }

                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    if (method.Name is ".ctor" or ".cctor")
                    {
                        foreach (var banned in BannedReferencesIn(method))
                        {
                            failures.Add($"{typeName}::{method.Name} reads {banned} at construction time");
                        }
                    }

                    foreach (var factory in FactoriesPassedToRegistrations(method))
                    {
                        foreach (var banned in BannedReferencesIn(factory))
                        {
                            failures.Add($"{typeName}::{method.Name} registers a DI factory ({factory.Name}) that reads {banned}");
                        }
                    }
                }
            }
        }

        return failures;
    }

    /// <summary>Every exemption must name an existing type in the given assemblies; an unknown name fails.</summary>
    public static IReadOnlyList<string> UnknownExemptions(IEnumerable<Assembly> assemblies)
    {
        var known = assemblies
            .SelectMany(a => a.GetTypes())
            .Select(t => t.FullName)
            .ToHashSet(StringComparer.Ordinal);

        return [.. Exemptions.Where(name => !known.Contains(name))];
    }

    private static IEnumerable<string> BannedReferencesIn(MethodDefinition method)
    {
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
            {
                continue;
            }

            if (ElementOf(instruction.Operand) is { } reference
                && BannedMembers.Contains((reference.DeclaringType.FullName, reference.Name)))
            {
                yield return reference.DeclaringType.FullName + "." + reference.Name;
            }
        }
    }

    private static IEnumerable<MethodDefinition> FactoriesPassedToRegistrations(MethodDefinition method)
    {
        var instructions = method.Body.Instructions;
        var registers = instructions.Any(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Newobj)
            && ElementOf(i.Operand) is { } reference
            && RegistrationTypes.Contains(reference.DeclaringType.FullName, StringComparer.Ordinal));
        if (!registers)
        {
            yield break;
        }

        foreach (var instruction in instructions)
        {
            if ((instruction.OpCode == OpCodes.Ldftn || instruction.OpCode == OpCodes.Ldvirtftn)
                && ElementOf(instruction.Operand)?.Resolve() is { HasBody: true } target
                && target.Parameters.Any(p => p.ParameterType.FullName == "System.IServiceProvider"))
            {
                yield return target;
            }
        }
    }

    private static MethodReference? ElementOf(object? operand) => operand switch
    {
        GenericInstanceMethod generic => generic.ElementMethod,
        MethodReference direct => direct,
        _ => null,
    };
}
