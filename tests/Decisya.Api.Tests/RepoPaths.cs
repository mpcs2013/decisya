using System.Reflection;

namespace Decisya.Api.Tests;

/// <summary>
/// Locates files relative to the repo root regardless of the test runner's working
/// directory. Mirrors <c>tests/Decisya.ServiceDefaults.Tests/RepoPaths.cs</c>; #22 folds
/// the duplicate into a shared test project.
/// </summary>
internal static class RepoPaths
{
    private static readonly string RepoRoot =
        FindRepoRootFromAssemblyMetadata() ?? FindRepoRootByWalkingUp(AppContext.BaseDirectory);

    public static string Find(string relativePath) => Path.Combine(RepoRoot, relativePath);

    private static string? FindRepoRootFromAssemblyMetadata()
    {
        var repoRoot = typeof(RepoPaths).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "Decisya.RepoRoot")
            ?.Value;

        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return null;
        }

        var directory = new DirectoryInfo(repoRoot);
        return directory.Exists && File.Exists(Path.Combine(directory.FullName, "decisya.slnx"))
            ? directory.FullName
            : null;
    }

    private static string FindRepoRootByWalkingUp(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "decisya.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException($"Could not locate the repo root (decisya.slnx) above {startDirectory}.");
    }
}
