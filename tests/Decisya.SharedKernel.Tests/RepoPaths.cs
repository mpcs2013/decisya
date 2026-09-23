using System.Runtime.CompilerServices;

namespace Decisya.SharedKernel.Tests;

/// <summary>
/// Locates files relative to the repo root regardless of the test runner's current
/// working directory (which differs between `dotnet test`, the Visual Studio Test
/// Explorer, and CI).
/// </summary>
internal static class RepoPaths
{
    private static readonly string RepoRoot = FindRepoRoot();

    /// <summary>Resolves <paramref name="relativePath"/> against the repo root.</summary>
    public static string Find(string relativePath) => Path.Combine(RepoRoot, relativePath);

    private static string FindRepoRoot([CallerFilePath] string callerFilePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(callerFilePath)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "decisya.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException($"Could not locate the repo root (decisya.slnx) above {callerFilePath}.");
    }
}
