using System.Reflection;
using Mono.Cecil;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #231, R7 (ADR-0024 point 1, option 3; G1 Story 4). No Wolverine envelope entity is mapped into any
/// EF model: <c>MapWolverineEnvelopeStorage</c> is never called, so the exact allow-list of unscoped entity
/// types stays empty and <c>TenantModelRule</c> (NFR-28) keeps no exception. The check is by method name, so
/// it holds without the <c>WolverineFx.EntityFrameworkCore</c> package being a dependency (it is not, and
/// adding it needs Marco's approval, ADR-0024 point 2). The mapping route that bypasses the helper, a
/// <c>modelBuilder.Entity&lt;Envelope&gt;()</c> in a <c>TenantDbContext</c>, fails <c>TenantModelRule</c>
/// itself (a fixture proves it).
/// </summary>
public static class EnvelopeStorageMappingRule
{
    internal const string BannedMethod = "MapWolverineEnvelopeStorage";

    /// <summary>One line per violation, naming the referencing type. Empty when the assemblies are clean.</summary>
    public static IReadOnlyList<string> Evaluate(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var failures = new List<string>();
        foreach (var assembly in assemblies)
        {
            using var definition = AssemblyDefinition.ReadAssembly(assembly.Location);

            foreach (var type in definition.MainModule.GetTypes().Where(t => !CompilerGeneratedTypeWalk.IsCompilerGenerated(t)))
            {
                foreach (var reference in MethodReferenceScan.ReferencesOf(type)
                    .Where(r => string.Equals(r.Name, BannedMethod, StringComparison.Ordinal))
                    .Select(r => r.DeclaringType.FullName)
                    .Distinct())
                {
                    failures.Add($"{MethodReferenceScan.ReflectionName(type)} references {reference}.{BannedMethod}");
                }
            }
        }

        return failures;
    }
}
