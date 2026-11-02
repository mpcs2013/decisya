namespace Decisya.ArchitectureTests;

/// <summary>
/// Every assembly the personal-data log rule of issue #230 runs against (architecture note D6): every
/// <c>src/**/*.csproj</c>, with no exclusion, so a new project is examined without editing the check. The
/// <see cref="KeyScope"/> assemblies are read by <c>Assembly.Location</c>. <c>Decisya.AppHost</c> and
/// <c>Decisya.Infrastructure.Migrator</c> are read from disk only (never loaded or referenced: a reference broke the
/// build with MSB3277 in #231). The scope fails closed: a missing DLL or PDB, or a DLL older than the newest
/// <c>*.cs</c> of its project, is a problem that names the project.
/// </summary>
internal static class LogScope
{
    /// <summary>The two projects read from disk only, exactly.</summary>
    public static IReadOnlyList<string> DiskOnly { get; } = KeyScope.Excluded;

    /// <summary>The assembly name of every <c>src</c> project, from its project file. No exclusion.</summary>
    public static IReadOnlyList<string> ProjectNames { get; } = [.. ProjectFiles().Keys.Order(StringComparer.Ordinal)];

    /// <summary>The DLL path of every project in the scope (an unresolved one is listed in <see cref="Problems"/>).</summary>
    public static IReadOnlyList<string> AssemblyPaths { get; } = [.. ProjectNames.Select(DllPathOf).OfType<string>()];

    /// <summary>One line per missing or stale project output. Empty when the scope is complete and fresh.</summary>
    public static IReadOnlyList<string> Problems { get; } = [.. ProjectNames.SelectMany(ProblemsOf)];

    /// <summary>
    /// The problems of one project output: a missing DLL or PDB, or a DLL older than the newest <c>*.cs</c> file of
    /// <paramref name="projectDirectory"/> (outside <c>bin</c> and <c>obj</c>).
    /// </summary>
    public static IReadOnlyList<string> ProblemsFor(string project, string? dllPath, string projectDirectory)
    {
        var problems = new List<string>();
        if (dllPath is null || !File.Exists(dllPath))
        {
            problems.Add($"{project}: build output not found; build the solution first (dotnet build).");
            return problems;
        }

        if (!File.Exists(Path.ChangeExtension(dllPath, ".pdb")))
        {
            problems.Add($"{project}: portable PDB not found next to the assembly; build the solution first (dotnet build).");
        }

        var newest = Directory.Exists(projectDirectory)
            ? Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
                .Where(path => !IsBuildFolder(path))
                .Select(File.GetLastWriteTimeUtc)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max()
            : DateTime.MinValue;

        if (File.GetLastWriteTimeUtc(dllPath) < newest)
        {
            problems.Add($"{project}: build output is older than its newest source file (stale); build the solution first (dotnet build).");
        }

        return problems;
    }

    private static IEnumerable<string> ProblemsOf(string project)
    {
        var directory = Path.GetDirectoryName(ProjectFiles()[project])!;
        return ProblemsFor(project, DllPathOf(project), directory);
    }

    private static string? DllPathOf(string project)
    {
        if (!DiskOnly.Contains(project, StringComparer.Ordinal))
        {
            var loaded = KeyScope.Assemblies.FirstOrDefault(a => string.Equals(a.GetName().Name, project, StringComparison.Ordinal));
            if (loaded is not null)
            {
                return loaded.Location;
            }
        }

        // The sibling of this test's own output: <artifacts>/bin/<Project>/<pivot>/<Project>.dll, which holds for
        // every ArtifactsPath form in Directory.Build.props.
        var baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var pivot = Path.GetFileName(baseDirectory);
        var bin = Path.GetDirectoryName(Path.GetDirectoryName(baseDirectory));
        if (bin is null)
        {
            return null;
        }

        var preferred = Path.Combine(bin, project, pivot, project + ".dll");
        if (File.Exists(preferred))
        {
            return preferred;
        }

        var projectBin = Path.Combine(bin, project);
        return Directory.Exists(projectBin)
            ? Directory.EnumerateFiles(projectBin, project + ".dll", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
    }

    private static Dictionary<string, string> ProjectFiles() => Directory
        .EnumerateFiles(RepoPaths.Find("src"), "*.csproj", SearchOption.AllDirectories)
        .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        .ToDictionary(path => Path.GetFileNameWithoutExtension(path), path => path, StringComparer.Ordinal);

    private static bool IsBuildFolder(string path)
    {
        var separator = Path.DirectorySeparatorChar;
        return path.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
            || path.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
            || path.Contains($"{separator}node_modules{separator}", StringComparison.Ordinal);
    }
}
