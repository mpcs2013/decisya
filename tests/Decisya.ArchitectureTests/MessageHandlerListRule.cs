using System.Reflection;
using System.Text.RegularExpressions;
using Decisya.SharedKernel.Tenancy;

namespace Decisya.ArchitectureTests;

/// <summary>A file under <c>tests/</c> that may hold a handler's isolation test.</summary>
public sealed record IsolationTestFile(string Name, string Text);

/// <summary>
/// Issue #231, R5 and G3 G4-231-03 (ADR-0024 point 5), plus G1 Story 6 scenario 2. A module that wants
/// message handlers exposes <c>public static IReadOnlyList&lt;Type&gt; MessageHandlerTypes</c> on its
/// <c>&lt;Name&gt;Module</c> class (architecture note, "Module handler surface") and the composition root
/// passes the lists to <c>AddDecisyaMessaging</c>. For every type a list names:
/// <list type="number">
/// <item>it is declared in the assembly of the module that lists it (a module cannot register another
/// module's handler, G1 Story 3 scenario 3; ADR-0005);</item>
/// <item>it carries no <see cref="AllowCrossTenantAttribute"/>: the HTTP admin role and MFA policy do not
/// apply to a message, so no cross-tenant handler is on a message path in #231 (G3 G4-231-03). The exact
/// allow-list <see cref="CrossTenantHandlerAllowList"/> is empty, and the first cross-tenant sender's G2 and
/// G3 add one exact entry;</item>
/// <item>a two-tenant isolation test names it (G1 Story 6 scenario 2; the <c>isolation-test</c> skill). The
/// convention: a file named <c>*IsolationTests.cs</c> under <c>tests/</c> that mentions the handler's
/// simple name as a whole word and carries the <c>Integration</c> category trait.</item>
/// </list>
/// </summary>
/// <remarks>
/// No module lists a handler in #231, so on <c>src</c> the rule is green because there is nothing to list;
/// its fixtures (red and green) are what prove it detects. R5's second half, that no module assembly
/// references the Wolverine assembly, is <see cref="WolverineReferences"/>.
/// </remarks>
public static class MessageHandlerListRule
{
    internal const string ListProperty = "MessageHandlerTypes";

    /// <summary>Exact handler type full names allowed to carry <c>[AllowCrossTenant]</c> on a message path. Empty in #231.</summary>
    internal static readonly IReadOnlySet<string> CrossTenantHandlerAllowList = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The <c>*IsolationTests.cs</c> files of the repository's <c>tests</c> tree.</summary>
    public static IReadOnlyList<IsolationTestFile> FromRepository()
    {
        var root = RepoPaths.Find(string.Empty);
        return
        [
            .. ScanExclusions.EnumerateFiles(root, RepoPaths.Find("tests"), "*IsolationTests.cs")
                .Select(path => new IsolationTestFile(Path.GetFileName(path), File.ReadAllText(path))),
        ];
    }

    /// <summary>One line per violation. Empty when every listed handler passes (or nothing is listed).</summary>
    public static IReadOnlyList<string> Evaluate(IEnumerable<Assembly> assemblies, IReadOnlyCollection<IsolationTestFile> testFiles)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        ArgumentNullException.ThrowIfNull(testFiles);

        var failures = new List<string>();
        foreach (var assembly in assemblies)
        {
            foreach (var module in assembly.GetTypes().Where(t => t.Name.EndsWith("Module", StringComparison.Ordinal)))
            {
                var property = module.GetProperty(ListProperty, BindingFlags.Public | BindingFlags.Static);
                if (property is null)
                {
                    continue;
                }

                if (property.GetValue(null) is not IEnumerable<Type> listed)
                {
                    failures.Add($"{module.FullName}.{ListProperty} is not an IReadOnlyList<Type> with a value");
                    continue;
                }

                foreach (var handler in listed)
                {
                    if (handler.Assembly != assembly)
                    {
                        failures.Add(
                            $"{module.FullName} lists {handler.FullName}, which is declared in {handler.Assembly.GetName().Name}, not in the module's own assembly");
                    }

                    if (handler.IsDefined(typeof(AllowCrossTenantAttribute), inherit: true)
                        && !CrossTenantHandlerAllowList.Contains(handler.FullName ?? handler.Name))
                    {
                        failures.Add($"{module.FullName} lists {handler.FullName}, which carries [AllowCrossTenant] (not allowed on a message path)");
                    }

                    if (!HasIsolationTest(handler, testFiles))
                    {
                        failures.Add($"{module.FullName} lists {handler.FullName}, which has no two-tenant isolation test");
                    }
                }
            }
        }

        return failures;
    }

    /// <summary>The names of the given assemblies that reference the <c>Wolverine</c> assembly (R5, second half).</summary>
    public static IReadOnlyList<string> WolverineReferences(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        return
        [
            .. assemblies
                .Where(assembly => assembly.GetReferencedAssemblies().Any(reference =>
                    string.Equals(reference.Name, "Wolverine", StringComparison.Ordinal)
                    || (reference.Name?.StartsWith("Wolverine.", StringComparison.Ordinal) ?? false)))
                .Select(assembly => assembly.GetName().Name ?? assembly.FullName ?? string.Empty),
        ];
    }

    private static bool HasIsolationTest(Type handler, IReadOnlyCollection<IsolationTestFile> testFiles)
    {
        var name = new Regex(@"\b" + Regex.Escape(handler.Name) + @"\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
        return testFiles.Any(file =>
            file.Name.EndsWith("IsolationTests.cs", StringComparison.Ordinal)
            && file.Text.Contains("\"Integration\"", StringComparison.Ordinal)
            && name.IsMatch(file.Text));
    }
}
