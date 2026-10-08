namespace Decisya.Identity.Tests;

/// <summary>
/// Issue #26 (G2 fix-up): the one place a repo-scanning test may skip a directory. Skips exactly
/// the repo-relative directory <c>src/Decisya.Web/node_modules</c> (npm's install output, tens of
/// thousands of third-party files). Matching is segment by segment on the path relative to the
/// repository root, never a substring of the absolute path and never "any folder named
/// node_modules", so it cannot hide a real violation elsewhere. <c>src/Decisya.Bff/wwwroot</c>
/// (our own build output) stays scanned. Duplicated per test project like <c>RepoPaths</c>.
/// Issue #121 adds one more exclusion: Python's git-ignored bytecode cache (see
/// <see cref="IsPythonBytecodeCache"/>), which the deploy tests create locally and which copies
/// the import-folder string out of the exact-path-exempt <c>stackguards.py</c>.
/// </summary>
internal static class ScanExclusions
{
    private const string ExcludedDirectory = "src/Decisya.Web/node_modules";

    private const string BytecodeCacheDirectory = "__pycache__";

    /// <summary>True when the repository-relative, '/'-separated path is the excluded directory or inside it, or a Python bytecode cache file.</summary>
    internal static bool IsExcluded(string relativePath) =>
        string.Equals(relativePath, ExcludedDirectory, StringComparison.Ordinal)
        || relativePath.StartsWith(ExcludedDirectory + "/", StringComparison.Ordinal)
        || IsPythonBytecodeCache(relativePath);

    /// <summary>
    /// True only for a file whose extension is exactly <c>.pyc</c> or <c>.pyo</c> (ordinal, lower
    /// case, as Python writes it) and whose immediate parent directory is named exactly
    /// <c>__pycache__</c> (ordinal). Both conditions are required, so a <c>.py</c> source file
    /// (even one planted inside a <c>__pycache__</c> folder), a sourceless <c>.pyc</c> next to the
    /// sources (importable, so launch-capable), a nested folder below <c>__pycache__</c>, and a
    /// lookalike such as <c>__pycache__x</c> or <c>my__pycache__</c> all stay scanned. Python
    /// never imports a bytecode file from <c>__pycache__</c> without its source, and the cache is
    /// git-ignored, so it cannot reach main.
    /// </summary>
    internal static bool IsPythonBytecodeCache(string relativePath)
    {
        if (!relativePath.EndsWith(".pyc", StringComparison.Ordinal)
            && !relativePath.EndsWith(".pyo", StringComparison.Ordinal))
        {
            return false;
        }

        var segments = relativePath.Split('/');
        return segments.Length >= 2
            && string.Equals(segments[^2], BytecodeCacheDirectory, StringComparison.Ordinal);
    }

    internal static bool IsExcluded(string repoRoot, string absolutePath) =>
        IsExcluded(Path.GetRelativePath(repoRoot, absolutePath).Replace(Path.DirectorySeparatorChar, '/'));

    /// <summary>Like <c>Directory.EnumerateFiles(scanRoot, pattern, AllDirectories)</c>, minus the excluded directory.</summary>
    internal static IEnumerable<string> EnumerateFiles(string repoRoot, string scanRoot, string pattern) =>
        Directory.EnumerateFiles(scanRoot, pattern, SearchOption.AllDirectories)
            .Where(file => !IsExcluded(repoRoot, file));
}
