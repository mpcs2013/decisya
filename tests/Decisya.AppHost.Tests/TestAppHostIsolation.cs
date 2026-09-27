using System.Diagnostics;

namespace Decisya.AppHost.Tests;

/// <summary>
/// Issue #17 G4 fix: a <c>Category=AppHost</c> test starts the real AppHost, which attaches
/// Postgres to a named data volume for Story 5's restart persistence, and (Marco's decision,
/// 2026-09-27) keeps both Postgres and Keycloak as persistent, fixed-name containers so a
/// real <c>dotnet run</c> reuses them across restarts. None of that must ever apply to a
/// test AppHost: it must never attach to, or stop, Marco's own dev volume
/// (<c>decisya-postgres-data</c>) or his persistent <c>decisya-postgres</c>/
/// <c>decisya-keycloak</c> containers. AppHost.cs reads both the volume name
/// (<c>Postgres:DataVolumeName</c>) and the ephemeral-container switch
/// (<c>AppHost:UseEphemeralContainers</c>) from configuration; every test that starts the
/// AppHost passes the overrides below and removes the throwaway volume afterwards.
/// </summary>
/// <remarks>
/// What happened without this: Marco's own AppHost was still running on
/// <c>decisya-postgres-data</c>, a test AppHost started a second Postgres server on the
/// same data directory, and Keycloak never became healthy. Separately, an orphaned,
/// non-persistent container left over from a hard AppHost stop kept writing to that same
/// volume while a later start wrote to it too, which corrupted it once already
/// (<c>PANIC: could not locate a valid checkpoint record</c>); Marco reset the volume.
/// </remarks>
internal static class TestAppHostIsolation
{
    private const string ProtectedVolumeName = "decisya-postgres-data";

    /// <summary>A fresh, unique data-volume name for one test run. Never <see cref="ProtectedVolumeName"/>.</summary>
    public static string CreateVolumeName() => $"decisya-apphosttests-{Guid.NewGuid():N}";

    /// <summary>
    /// The command-line arguments <c>DistributedApplicationTestingBuilder.CreateAsync</c>
    /// passes through to <c>AppHost.cs</c>'s own configuration: the throwaway volume name,
    /// and <c>AppHost:UseEphemeralContainers=true</c> so postgres/keycloak get Aspire's
    /// default Session lifetime and an auto-generated, unique container name instead of
    /// Marco's persistent, fixed-name dev containers.
    /// </summary>
    public static string[] AsCommandLineArgs(string volumeName) =>
        [$"--Postgres:DataVolumeName={volumeName}", "--AppHost:UseEphemeralContainers=true"];

    /// <summary>
    /// Best-effort cleanup of the throwaway volume, with short retries for the brief
    /// container-removal race right after the AppHost's own disposal. Refuses, defensively,
    /// to ever remove <see cref="ProtectedVolumeName"/> — on top of every caller already
    /// only ever passing a name from <see cref="CreateVolumeName"/>.
    /// </summary>
    public static async Task RemoveVolumeAsync(string volumeName, CancellationToken cancellationToken)
    {
        if (string.Equals(volumeName, ProtectedVolumeName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to remove '{ProtectedVolumeName}': that is Marco's own dev data volume, never a test's.");
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var exitCode = await RunDockerAsync(["volume", "rm", volumeName], cancellationToken);
            if (exitCode == 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        // Best-effort: a leftover throwaway volume (named uniquely, never
        // decisya-postgres-data) is a minor disk-space nuisance, not a correctness issue,
        // so this does not fail the test.
    }

    private static async Task<int> RunDockerAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start 'docker'.");
        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        _ = await stdOutTask;
        _ = await stdErrTask;
        return process.ExitCode;
    }
}
