using System.Reflection;
using Mono.Cecil;
using NetArchTest.Rules;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #341, G6-341-05 (c), G3 M341-02 (c), threat T341-02. A bulk statement (<c>ExecuteUpdate(Async)</c>,
/// <c>ExecuteDelete(Async)</c>) bypasses <c>SaveChanges</c> and its guards, so the Ledger module is allowed exactly
/// one: the book-row lock in <c>CreateAccountHandler.LockBookAsync</c>. The rule collects every call site of the two
/// bulk-update names and the two bulk-delete names in the types it covers (including compiler-generated closures and
/// async state machines, credited to the method a human wrote) and fails when:
/// <list type="bullet">
/// <item><description>there is any <c>ExecuteDelete(Async)</c> site;</description></item>
/// <item><description>the <c>ExecuteUpdate(Async)</c> sites are not exactly the one expected site (a second site, a
/// moved site or a missing site all fail).</description></item>
/// </list>
/// </summary>
public static class BulkStatementCallSiteRule
{
    /// <summary>The one permitted bulk-update site in the Ledger module, as <c>Namespace.Type.Method</c>.</summary>
    public const string LedgerLockSite = "Decisya.Modules.Ledger.Application.CreateAccountHandler.LockBookAsync";

    private static readonly HashSet<string> UpdateNames = ["ExecuteUpdate", "ExecuteUpdateAsync"];
    private static readonly HashSet<string> DeleteNames = ["ExecuteDelete", "ExecuteDeleteAsync"];

    /// <summary>The outcome: the sites found and one line per violation.</summary>
    public sealed record Outcome(IReadOnlyList<string> UpdateSites, IReadOnlyList<string> DeleteSites, IReadOnlyList<string> Violations)
    {
        public bool IsSuccessful => Violations.Count == 0;
    }

    public static Outcome Evaluate(string expectedUpdateSite, params Assembly[] assemblies) =>
        EvaluateIn(null, expectedUpdateSite, assemblies);

    /// <summary>The rule over the types in <paramref name="namespacePrefix"/> (any when <see langword="null"/>); used by the fixture tests.</summary>
    public static Outcome EvaluateIn(string? namespacePrefix, string expectedUpdateSite, params Assembly[] assemblies)
    {
        var collector = new Collector();
        var types = Types.InAssemblies(assemblies);

        // The collector never fails a type: the verdict is over the whole set of sites below.
        _ = (namespacePrefix is null ? types.Should() : types.That().ResideInNamespace(namespacePrefix).Should())
            .MeetCustomRule(collector)
            .GetResult();

        var updates = collector.UpdateSites.Distinct().Order(StringComparer.Ordinal).ToList();
        var deletes = collector.DeleteSites.Distinct().Order(StringComparer.Ordinal).ToList();
        var violations = new List<string>();

        violations.AddRange(deletes.Select(site => $"{site}: a bulk delete (no ExecuteDelete is allowed)"));

        if (updates.Count != 1)
        {
            violations.Add($"expected exactly one ExecuteUpdate site ({expectedUpdateSite}), found {updates.Count}: {string.Join(", ", updates)}");
        }
        else if (!string.Equals(updates[0], expectedUpdateSite, StringComparison.Ordinal))
        {
            violations.Add($"the one ExecuteUpdate site is {updates[0]}, not {expectedUpdateSite}");
        }

        return new Outcome(updates, deletes, violations);
    }

    private sealed class Collector : ICustomRule
    {
        public List<string> UpdateSites { get; } = [];

        public List<string> DeleteSites { get; } = [];

        public bool MeetsRule(TypeDefinition type)
        {
            foreach (var (containing, method) in CompilerGeneratedTypeWalk.MethodsIncludingCompilerGeneratedDescendants(type))
            {
                if (!method.HasBody)
                {
                    continue;
                }

                foreach (var instruction in method.Body.Instructions)
                {
                    var reference = instruction.Operand switch
                    {
                        GenericInstanceMethod generic => generic.ElementMethod,
                        MethodReference direct => direct,
                        _ => null,
                    };

                    if (reference is null || reference.DeclaringType.FullName is not { } declaring
                        || !declaring.StartsWith("Microsoft.EntityFrameworkCore.", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (UpdateNames.Contains(reference.Name))
                    {
                        UpdateSites.Add(SiteName(type, containing, method));
                    }
                    else if (DeleteNames.Contains(reference.Name))
                    {
                        DeleteSites.Add(SiteName(type, containing, method));
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// <c>Namespace.Type.Method</c> of the method a human wrote: a lambda in a closure is named
        /// <c>&lt;Method&gt;b__0_0</c> and an async body lives in <c>MoveNext</c> of <c>&lt;Method&gt;d__N</c>.
        /// </summary>
        private static string SiteName(TypeDefinition credited, TypeDefinition containing, MethodDefinition method)
        {
            var raw = method.Name == "MoveNext" ? containing.Name : method.Name;
            var name = raw.StartsWith('<') && raw.IndexOf('>', StringComparison.Ordinal) is var close and > 1
                ? raw[1..close]
                : raw;
            return $"{MethodReferenceScan.ReflectionName(credited)}.{name}";
        }
    }
}
