namespace Decisya.SharedKernel.Tests;

/// <summary>
/// Locates files relative to the repo root regardless of the test runner's current
/// working directory (which differs between `dotnet test`, the Visual Studio Test
/// Explorer, and CI).
/// </summary>
/// <remarks>
/// The search starts from the test assembly's output folder (<c>tests/…/bin/…</c>), not
/// from <c>[CallerFilePath]</c>: CI builds set <c>ContinuousIntegrationBuild</c>
/// (Directory.Build.props), which maps source paths to <c>/_/</c>, so a compile-time
/// source path does not exist on the runner.
/// </remarks>
internal static class RepoPaths
{
    private static readonly string RepoRoot = FindRepoRoot(AppContext.BaseDirectory);

    /// <summary>Resolves <paramref name="relativePath"/> against the repo root.</summary>
    public static string Find(string relativePath) => Path.Combine(RepoRoot, relativePath);

    private static string FindRepoRoot(string startDirectory)
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
