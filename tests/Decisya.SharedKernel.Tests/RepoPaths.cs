using System.Reflection;

namespace Decisya.SharedKernel.Tests;

/// <summary>
/// Locates files relative to the repo root regardless of the test runner's current
/// working directory (which differs between `dotnet test`, the Visual Studio Test
/// Explorer, and CI).
/// </summary>
/// <remarks>
/// Resolution order:
/// <list type="number">
/// <item><description>
/// The <c>Decisya.RepoRoot</c> <see cref="AssemblyMetadataAttribute"/> that
/// Directory.Build.props stamps onto every assembly (ADR-0010, issue #36). This is the
/// primary path once <c>UseArtifactsOutput</c> moves build output out from under
/// <c>tests/…</c> (and, in the sandbox and on the Windows host, out of the working tree
/// entirely): the walk-up below can no longer reach <c>decisya.slnx</c> from there. The
/// attribute's value is verified to still be a directory containing
/// <c>decisya.slnx</c> before it is trusted, rather than used blindly (e.g. a stale value
/// on a copied or relocated assembly).
/// </description></item>
/// <item><description>
/// Falls back to walking up from the test assembly's output folder
/// (<c>AppContext.BaseDirectory</c>, e.g. <c>tests/…/bin/…</c>) to <c>decisya.slnx</c>,
/// for any build that does not stamp the attribute.
/// </description></item>
/// </list>
/// Neither uses <c>[CallerFilePath]</c>: CI builds set <c>ContinuousIntegrationBuild</c>
/// (Directory.Build.props), which maps source paths to <c>/_/</c>, so a compile-time
/// source path does not exist on the runner.
/// </remarks>
internal static class RepoPaths
{
    private static readonly string RepoRoot =
        FindRepoRootFromAssemblyMetadata() ?? FindRepoRootByWalkingUp(AppContext.BaseDirectory);

    /// <summary>Resolves <paramref name="relativePath"/> against the repo root.</summary>
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
