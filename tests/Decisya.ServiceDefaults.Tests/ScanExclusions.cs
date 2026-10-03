namespace Decisya.ServiceDefaults.Tests;

/// <summary>
/// Issue #26 (G2 fix-up): the one place a repo-scanning test may skip a directory. Skips exactly
/// the repo-relative directory <c>src/Decisya.Web/node_modules</c> (npm's install output, tens of
/// thousands of third-party files). Matching is segment by segment on the path relative to the
/// repository root, never a substring of the absolute path and never "any folder named
/// node_modules", so it cannot hide a real violation elsewhere. <c>src/Decisya.Bff/wwwroot</c>
/// (our own build output) stays scanned. Duplicated per test project like <c>RepoPaths</c>.
/// </summary>
internal static class ScanExclusions
{
    private const string ExcludedDirectory = "src/Decisya.Web/node_modules";

    /// <summary>True when the repository-relative, '/'-separated path is the excluded directory or inside it.</summary>
    internal static bool IsExcluded(string relativePath) =>
        string.Equals(relativePath, ExcludedDirectory, StringComparison.Ordinal)
        || relativePath.StartsWith(ExcludedDirectory + "/", StringComparison.Ordinal);

    internal static bool IsExcluded(string repoRoot, string absolutePath) =>
        IsExcluded(Path.GetRelativePath(repoRoot, absolutePath).Replace(Path.DirectorySeparatorChar, '/'));

    /// <summary>Like <c>Directory.EnumerateFiles(scanRoot, pattern, AllDirectories)</c>, minus the excluded directory.</summary>
    internal static IEnumerable<string> EnumerateFiles(string repoRoot, string scanRoot, string pattern) =>
        Directory.EnumerateFiles(scanRoot, pattern, SearchOption.AllDirectories)
            .Where(file => !IsExcluded(repoRoot, file));
}
