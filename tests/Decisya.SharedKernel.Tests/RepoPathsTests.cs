namespace Decisya.SharedKernel.Tests;

/// <summary>
/// Issue #36 (ADR-0010): RepoPaths must still resolve the repo root once build output moves
/// out from under <c>tests/…</c> (UseArtifactsOutput), when the AppContext.BaseDirectory
/// walk-up can no longer reach it and RepoPaths must fall back to the Decisya.RepoRoot
/// AssemblyMetadataAttribute instead.
/// </summary>
[Trait("Category", "Unit")]
public class RepoPathsTests
{
    [Fact]
    public void Find_resolves_a_relative_path_against_a_directory_that_contains_decisya_slnx()
    {
        var resolved = RepoPaths.Find("decisya.slnx");

        File.Exists(resolved).Should().BeTrue(
            $"RepoPaths should resolve \"decisya.slnx\" to an existing file at {resolved}, " +
            "proving the directory it resolved against is the repo root");
    }
}
