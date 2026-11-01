using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #231, R10 and R11 (G1 Story 5; ADR-0001 "keys are prefixed with the tenant id"; closes register
/// item C-14; G3 G4-231-05). Every Redis or object-storage key that holds a tenant is built by
/// <c>Decisya.SharedKernel.Tenancy.TenantKey</c>, nowhere else. Until the storage wrapper takes a
/// <c>TenantKey</c> parameter (a recorded criterion for the storage issue), this rule is a tripwire, not a
/// proof (G3 T-15).
/// </summary>
/// <remarks>
/// <para>
/// <b>Sinks</b> are members of types in <c>StackExchange.Redis</c>, <c>Microsoft.Extensions.Caching.Distributed</c>,
/// <c>Microsoft.Extensions.Caching.Hybrid</c>, <c>Microsoft.Extensions.Caching.StackExchangeRedis</c>,
/// <c>Amazon.S3</c> and <c>Minio</c>; the storage issue extends the list. For every type in the scope that
/// references a sink:
/// </para>
/// <list type="number">
/// <item><b>Per method</b> (a closure or state machine body is its own method): each method that references a
/// sink member also references <c>TenantKey.ToCacheKey</c> or <c>TenantKey.ToObjectKey</c>. A type with one
/// compliant method and one plain-string sink call fails, naming the second method (G3 G4-231-05 item 3). A
/// type on the exact exemption list skips this check.</item>
/// <item><b>Per type</b>: no tenant text beside a sink: <c>TenantId.get_Value</c>, <c>TenantId.ToString</c>,
/// <c>TenantResolution.ToString</c>, <c>TenantResolution.get_TenantId</c>, <c>box</c> or <c>constrained.</c> of
/// either type, or a generic method instantiated over either (such as
/// <c>AppendFormatted&lt;TenantId&gt;</c> from an interpolated string). Exempt types are checked too.</item>
/// <item><b>Claims</b>: a non-exempt type references no <c>System.Security.Claims</c> member. An exempt type
/// (session-keyed, no tenant) may read claims, but never the <c>tenant_id</c> claim (an <c>ldstr</c> or constant
/// equal to it) (G3 T-16).</item>
/// </list>
/// <para>
/// Nested types: a nested type counts as its outermost declaring type for the exemption (Marco, option A,
/// 2026-11-01), so <c>RedisRefreshLock+LockHandle</c> and its state machines are covered by the exact name
/// <c>RedisRefreshLock</c>. A nested type of a non-exempt type is still checked.
/// </para>
/// <para>
/// The exemptions are exact type full names, and each must be an existing type in the scope: a namespace, a
/// wildcard or an unknown name fails (<see cref="UnknownExemptions"/>). The Redis session ticket store keys by
/// session and holds no tenant (G1 Story 5, "the session ticket store is not flagged").
/// </para>
/// </remarks>
public static class TenantKeyRule
{
    internal const string TenantKeyType = "Decisya.SharedKernel.Tenancy.TenantKey";

    private const string TenantIdType = "Decisya.SharedKernel.Tenancy.TenantId";
    private const string TenantResolutionType = "Decisya.SharedKernel.Tenancy.TenantResolution";
    private const string TenantClaim = "tenant_id";
    private const string ClaimsNamespacePrefix = "System.Security.Claims.";

    internal static readonly IReadOnlyList<string> SinkNamespaces =
    [
        "StackExchange.Redis",
        "Microsoft.Extensions.Caching.Distributed",
        "Microsoft.Extensions.Caching.Hybrid",
        "Microsoft.Extensions.Caching.StackExchangeRedis",
        "Amazon.S3",
        "Minio",
    ];

    /// <summary>The Redis session ticket store and its two helpers: keyed by session, no tenant.</summary>
    internal static readonly IReadOnlySet<string> Exemptions = new HashSet<string>(StringComparer.Ordinal)
    {
        "Decisya.Bff.Session.RedisTicketStore",
        "Decisya.Bff.Session.RedisRefreshLock",
        "Decisya.Bff.Session.RedisRegistration",
    };

    /// <summary>One line per violation. Empty when the assemblies are clean.</summary>
    public static IReadOnlyList<string> Evaluate(IEnumerable<Assembly> assemblies) => Evaluate(assemblies, Exemptions);

    internal static IReadOnlyList<string> Evaluate(IEnumerable<Assembly> assemblies, IReadOnlySet<string> exemptions)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        ArgumentNullException.ThrowIfNull(exemptions);

        var failures = new List<string>();
        foreach (var assembly in assemblies)
        {
            using var definition = AssemblyDefinition.ReadAssembly(assembly.Location);

            foreach (var type in definition.MainModule.GetTypes().Where(t => !CompilerGeneratedTypeWalk.IsCompilerGenerated(t)))
            {
                var typeName = MethodReferenceScan.ReflectionName(type);
                var outermost = type;
                while (outermost.DeclaringType is not null)
                {
                    outermost = outermost.DeclaringType;
                }

                // Marco, option A (2026-11-01): a nested type counts as its outermost declaring type for the
                // exemption (compiler-generated types already count as their declaring type), as R14 does.
                var exempt = exemptions.Contains(MethodReferenceScan.ReflectionName(outermost));

                var methods = CompilerGeneratedTypeWalk.MethodsIncludingCompilerGeneratedDescendants(type)
                    .Where(entry => entry.Method.HasBody)
                    .ToList();

                var referencesASink = false;
                foreach (var (owner, method) in methods)
                {
                    var sink = FirstSinkReferencedBy(method);
                    if (sink is null)
                    {
                        continue;
                    }

                    referencesASink = true;
                    if (!exempt && !ReferencesTenantKey(method))
                    {
                        var site = ReferenceEquals(owner, type) ? method.Name : owner.Name + "." + method.Name;
                        failures.Add(
                            $"{typeName}::{site} references a key sink ({sink}) without TenantKey.ToCacheKey or TenantKey.ToObjectKey");
                    }
                }

                if (!referencesASink)
                {
                    continue;
                }

                foreach (var text in methods.SelectMany(entry => TenantTextIn(entry.Method)).Distinct())
                {
                    failures.Add($"{typeName} references tenant text ({text}) next to a key sink");
                }

                if (methods.Any(entry => ReadsTheTenantClaim(entry.Method)))
                {
                    failures.Add($"{typeName} reads the {TenantClaim} claim next to a key sink");
                }

                if (!exempt)
                {
                    foreach (var claim in methods.SelectMany(entry => ClaimsMembersIn(entry.Method)).Distinct())
                    {
                        failures.Add($"{typeName} references System.Security.Claims ({claim}) next to a key sink");
                    }
                }
            }
        }

        return failures;
    }

    /// <summary>Every exemption must name an existing type in the given assemblies; a namespace, a wildcard or an unknown name fails.</summary>
    public static IReadOnlyList<string> UnknownExemptions(IEnumerable<Assembly> assemblies, IReadOnlySet<string>? exemptions = null)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var known = assemblies
            .SelectMany(a => a.GetTypes())
            .Select(t => t.FullName)
            .ToHashSet(StringComparer.Ordinal);

        return [.. (exemptions ?? Exemptions).Where(name => !known.Contains(name))];
    }

    private static string? FirstSinkReferencedBy(MethodDefinition method)
    {
        foreach (var instruction in method.Body.Instructions)
        {
            if (IlScan.MethodOperand(instruction.Operand) is not { } reference)
            {
                continue;
            }

            var declaring = IlScan.ElementOf(reference.DeclaringType).FullName;
            if (SinkNamespaces.Any(ns => declaring.StartsWith(ns + ".", StringComparison.Ordinal)))
            {
                return declaring + "." + reference.Name;
            }
        }

        return null;
    }

    private static bool ReferencesTenantKey(MethodDefinition method) =>
        method.Body.Instructions.Any(instruction =>
            IlScan.MethodOperand(instruction.Operand) is { } reference
            && string.Equals(IlScan.ElementOf(reference.DeclaringType).FullName, TenantKeyType, StringComparison.Ordinal)
            && reference.Name is "ToCacheKey" or "ToObjectKey");

    private static IEnumerable<string> TenantTextIn(MethodDefinition method)
    {
        var instructions = method.Body.Instructions;
        for (var index = 0; index < instructions.Count; index++)
        {
            var instruction = instructions[index];
            if (IlScan.MethodOperand(instruction.Operand) is { } reference)
            {
                var declaring = IlScan.ElementOf(reference.DeclaringType).FullName;

                if (string.Equals(declaring, TenantIdType, StringComparison.Ordinal) && reference.Name is "get_Value" or "ToString")
                {
                    yield return "TenantId." + reference.Name;
                }
                else if (string.Equals(declaring, TenantResolutionType, StringComparison.Ordinal)
                    && reference.Name is "ToString" or "get_TenantId")
                {
                    yield return "TenantResolution." + reference.Name;
                }
                else if (instruction.Operand is GenericInstanceMethod generic
                    && generic.GenericArguments.Any(argument => IlScan.Flatten(argument).Any(IsTenantType)))
                {
                    yield return "generic instantiation over a tenant type, " + reference.Name;
                }
            }

            if ((instruction.OpCode == OpCodes.Box || instruction.OpCode == OpCodes.Constrained)
                && instruction.Operand is TypeReference typeOperand
                && IsTenantType(typeOperand))
            {
                // A constrained call names the member it calls next (for example Object.ToString on the struct).
                var next = index + 1 < instructions.Count ? IlScan.MethodOperand(instructions[index + 1].Operand) : null;
                yield return instruction.OpCode == OpCodes.Constrained && next is not null
                    ? typeOperand.Name + "." + next.Name + " (constrained call)"
                    : instruction.OpCode.Name + " " + typeOperand.Name;
            }
        }
    }

    private static bool IsTenantType(TypeReference type) =>
        string.Equals(type.FullName, TenantIdType, StringComparison.Ordinal)
        || string.Equals(type.FullName, TenantResolutionType, StringComparison.Ordinal);

    private static bool ReadsTheTenantClaim(MethodDefinition method) =>
        method.Body.Instructions.Any(instruction =>
            instruction.OpCode == OpCodes.Ldstr
            && instruction.Operand is string text
            && string.Equals(text, TenantClaim, StringComparison.Ordinal));

    private static IEnumerable<string> ClaimsMembersIn(MethodDefinition method)
    {
        foreach (var instruction in method.Body.Instructions)
        {
            if (IlScan.MethodOperand(instruction.Operand) is { } reference
                && IlScan.ElementOf(reference.DeclaringType).FullName.StartsWith(ClaimsNamespacePrefix, StringComparison.Ordinal))
            {
                yield return reference.DeclaringType.Name + "." + reference.Name;
            }
        }
    }
}
