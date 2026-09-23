using System.Diagnostics;

namespace Decisya.SharedKernel.Tests.BannedApi;

/// <summary>
/// Story 5, Scenarios 1 and 2 (and Story 4, Scenario 4) — genuinely proves RS0030
/// fires, by invoking a real <c>dotnet build</c> against a standalone, intentionally
/// non-compiling fixture project (Fixtures/BannedApiFixture) that calls every member
/// listed in BannedSymbols.txt. This exercises the actual enforcement path (the same
/// one CI runs via <c>dotnet test</c>), not a test that merely reads BannedSymbols.txt
/// (see <see cref="BannedSymbolsSyncTests"/> for that separate AC).
/// </summary>
public class BannedApiCompilationTests
{
    private static readonly string[] ExpectedCorrectiveMessageFragments =
    [
        "Use NodaTime IClock via SharedKernel",
        "Money is decimal; never convert amounts to double",
    ];

    [Fact]
    public void Building_the_banned_api_fixture_fails_with_RS0030_for_every_banned_member()
    {
        var fixtureProject = RepoPaths.Find(Path.Combine(
            "tests", "Decisya.SharedKernel.Tests", "Fixtures", "BannedApiFixture", "BannedApiFixture.csproj"));

        File.Exists(fixtureProject).Should().BeTrue($"the fixture project must exist at {fixtureProject}");

        var (exitCode, output) = RunDotnetBuild(fixtureProject);

        exitCode.Should().NotBe(0, "the fixture calls only banned members and must fail to build:\n" + output);

        // One RS0030 diagnostic per banned member call in BannedCalls.cs.
        CountOccurrences(output, "RS0030").Should().BeGreaterThanOrEqualTo(6, output);

        foreach (var fragment in ExpectedCorrectiveMessageFragments)
        {
            output.Should().Contain(fragment);
        }
    }

    private static (int ExitCode, string Output) RunDotnetBuild(string projectPath)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("-v:normal");
        // Without this, the MSBuild node this spawns can outlive the test process and
        // keep holding file locks on the fixture's build output.
        startInfo.ArgumentList.Add("-nodeReuse:false");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start 'dotnet build' for the banned-API fixture.");

        // Read asynchronously while waiting, to avoid a deadlock if either stream's
        // buffer fills before the process exits.
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        var exited = process.WaitForExit(TimeSpan.FromMinutes(3));
        if (!exited)
        {
            // Kill the whole tree (not just the outer "dotnet" process) before failing,
            // so a hung build never leaks an MSBuild worker holding file locks.
            process.Kill(entireProcessTree: true);
        }

        exited.Should().BeTrue("dotnet build of the banned-API fixture should complete within 3 minutes");

        var output = stdOutTask.GetAwaiter().GetResult() + stdErrTask.GetAwaiter().GetResult();
        return (process.ExitCode, output);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
