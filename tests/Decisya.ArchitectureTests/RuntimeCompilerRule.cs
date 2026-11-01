using System.Reflection;
using System.Text.RegularExpressions;

namespace Decisya.ArchitectureTests;

/// <summary>A file's repository-relative path (or any label) and its text.</summary>
public sealed record SourceFile(string RelativePath, string Text);

/// <summary>
/// Issue #231, R15 (ADR-0024 amendment 1 item 5; G3 S-11). The Wolverine runtime compiler (Roslyn) stays out
/// of every <c>src</c> project: the Api runs <c>TypeLoadMode.Static</c>, and
/// <c>WolverineFx.RuntimeCompilation</c> is approved for <c>tests/Decisya.Api.Tests</c> only. Three checks,
/// each red on a synthetic input in <c>MessagingBoundaryTests</c> and green on the real <c>src</c> tree:
/// no <c>src/**/*.csproj</c> has a <c>PackageReference</c> to either package; no
/// <c>src/**/packages.lock.json</c> lists either (a <c>PackageReference</c> scan misses a transitive
/// arrival through another package, S-11); and no scope assembly references either assembly.
/// </summary>
public static class RuntimeCompilerRule
{
    /// <summary>The package ids that must not appear under <c>src</c>.</summary>
    internal static readonly IReadOnlyList<string> BannedPackages = ["WolverineFx.RuntimeCompilation", "JasperFx.RuntimeCompiler"];

    /// <summary>The assembly names that must not be referenced by a <c>src</c> assembly.</summary>
    internal static readonly IReadOnlyList<string> BannedAssemblies = ["Wolverine.RuntimeCompilation", "JasperFx.RuntimeCompiler"];

    /// <summary>The <c>*.csproj</c> files under <c>src</c>.</summary>
    public static IReadOnlyList<SourceFile> ProjectFilesUnderSrc() => FilesUnderSrc("*.csproj");

    /// <summary>The <c>packages.lock.json</c> files under <c>src</c>.</summary>
    public static IReadOnlyList<SourceFile> LockFilesUnderSrc() => FilesUnderSrc("packages.lock.json");

    /// <summary>Project files with a <c>PackageReference</c> to a banned package.</summary>
    public static IReadOnlyList<string> ProjectFileViolations(IEnumerable<SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var reference = new Regex(
            @"<PackageReference\s[^>]*\b(Include|Update)\s*=\s*""(" + AlternationOfPackages() + @")""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5));

        return [.. files.Where(file => reference.IsMatch(file.Text)).Select(file => file.RelativePath)];
    }

    /// <summary>Lock files that list a banned package, directly or transitively.</summary>
    public static IReadOnlyList<string> LockFileViolations(IEnumerable<SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var package = new Regex(
            @"""(" + AlternationOfPackages() + @")""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5));

        return [.. files.Where(file => package.IsMatch(file.Text)).Select(file => file.RelativePath)];
    }

    /// <summary>The assemblies of the scope that reference a banned assembly, as <c>assembly -&gt; reference</c>.</summary>
    public static IReadOnlyList<string> AssemblyViolations(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        return
        [
            .. assemblies.SelectMany(assembly => BannedReferenceNames(assembly.GetReferencedAssemblies().Select(r => r.Name))
                .Select(banned => $"{assembly.GetName().Name} -> {banned}")),
        ];
    }

    /// <summary>Which of the given referenced-assembly names are banned (ordinal, exact).</summary>
    public static IReadOnlyList<string> BannedReferenceNames(IEnumerable<string?> referencedNames)
    {
        ArgumentNullException.ThrowIfNull(referencedNames);

        return
        [
            .. referencedNames
                .OfType<string>()
                .Where(name => BannedAssemblies.Contains(name, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal),
        ];
    }

    private static string AlternationOfPackages() => string.Join('|', BannedPackages.Select(Regex.Escape));

    private static List<SourceFile> FilesUnderSrc(string pattern)
    {
        var root = RepoPaths.Find(string.Empty);
        var separator = Path.DirectorySeparatorChar;

        return
        [
            .. ScanExclusions.EnumerateFiles(root, RepoPaths.Find("src"), pattern)
                .Where(path => !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
                    && !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal))
                .Select(path => new SourceFile(Path.GetRelativePath(root, path), File.ReadAllText(path))),
        ];
    }
}
