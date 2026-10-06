using System.Text.RegularExpressions;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Decisya.AppHost.Tests;

/// <summary>
/// Issue #21 (0.09 Modules.Tenancy), G2's AppHost wiring and G3 G4-21-05: starts the real
/// AppHost and proves that <c>decisya-api</c> waits for <c>decisya-migrator</c> to finish and
/// then carries exactly two <c>ConnectionStrings__*</c> environment keys since #23
/// (<c>ConnectionStrings__tenancy</c> for the least-privilege <c>decisya_tenancy</c> role and
/// <c>ConnectionStrings__entitlements</c> for the least-privilege <c>decisya_entitlements</c>
/// role) — never the owner connection <c>decisya-migrator</c> itself receives. Needs DCP, the Aspire
/// CLI bundle and a running Docker daemon, so it runs on Marco's host only (ADR-0010), like
/// every other class in this project.
/// </summary>
[Trait("Category", "AppHost")]
public class TenancyMigratorResourceTests
{
    private const string ApiResourceName = "decisya-api";
    private const string MigratorResourceName = "decisya-migrator";

    // T-12 (G3): none of these owner/superuser-carrying names may ever reach decisya-api.
    private static readonly string[] OwnerCredentialEnvironmentKeysUsedElsewhereInTheAppHost =
    [
        "ConnectionStrings__decisya",
        "ConnectionStrings__postgres",
        "Migrator__TenancyRolePassword",
        "Migrator__EntitlementsRolePassword",
    ];

    [Fact]
    public async Task Decisya_api_waits_for_the_migrator_and_gets_only_the_least_privilege_tenancy_connection_string()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var volumeName = TestAppHostIsolation.CreateVolumeName();
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Decisya_AppHost>(
            TestAppHostIsolation.AsCommandLineArgs(volumeName), cancellationToken);
        var app = await appHost.BuildAsync(cancellationToken);

        try
        {
            // Bounded: an unbounded StartAsync let a startup hang run for hours (#120 lesson).
            using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var startLinked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, startTimeout.Token);
            try
            {
                await app.StartAsync(startLinked.Token);
            }
            catch (OperationCanceledException) when (startTimeout.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "AppHost StartAsync (TenancyMigratorResourceTests: start the AppHost) did not complete within the " +
                    $"bounded wait (5 minutes). This run used the throwaway Postgres volume '{volumeName}'.");
            }

            var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
            // decisya-api carries WaitForCompletion(migrator) (G2), so reaching Running here
            // is itself evidence the migrator resource ran to completion successfully first.
            using var runningTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var runningLinked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runningTimeout.Token);
            try
            {
                await notifications.WaitForResourceAsync(ApiResourceName, KnownResourceStates.Running, runningLinked.Token);
            }
            catch (OperationCanceledException) when (runningTimeout.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"'{ApiResourceName}' did not reach Running within the bounded wait (3 minutes). " +
                    $"Confirm '{MigratorResourceName}' completed (migrated the schema and provisioned " +
                    "decisya_tenancy) and that no other AppHost instance is running, using its own " +
                    $"throwaway Postgres volume ('{volumeName}'), not Marco's 'decisya-postgres-data'.");
            }

            await AssertResourceWiringAsync(app, cancellationToken);

            await app.StopAsync(cancellationToken);
        }
        finally
        {
            await app.DisposeAsync();
            await TestAppHostIsolation.RemoveVolumeAsync(volumeName, CancellationToken.None);
        }
    }

    private static async Task AssertResourceWiringAsync(DistributedApplication app, CancellationToken cancellationToken)
    {
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var apiResource = model.Resources.OfType<IResourceWithEnvironment>().Single(r => r.Name == ApiResourceName);
        var migratorResource = model.Resources.OfType<IResourceWithEnvironment>().Single(r => r.Name == MigratorResourceName);

#pragma warning disable CS0618 // see AppHostResourceTests: advisory-deprecated in Aspire.Hosting.Testing 13.5.4, reported at G6.
        var apiVariables = await apiResource.GetEnvironmentVariableValuesAsync(DistributedApplicationOperation.Run);
        var migratorVariables = await migratorResource.GetEnvironmentVariableValuesAsync(DistributedApplicationOperation.Run);
#pragma warning restore CS0618

        // G4-21-05 / G4-23-04: decisya-api's ConnectionStrings__* set is exactly
        // {tenancy, entitlements}, each least-privilege.
        var apiConnectionStringKeys = apiVariables.Keys
            .Where(key => key.StartsWith("ConnectionStrings__", StringComparison.Ordinal))
            .ToList();
        apiConnectionStringKeys.Should().BeEquivalentTo(
            ["ConnectionStrings__tenancy", "ConnectionStrings__entitlements"]);

        // L-1 (G6 review, carried from AppHostResourceTests): never assert on, or print, a
        // connection string itself — it carries a role password. Only the "Username=..."
        // segment is ever extracted and compared, so a failure message can never leak it.
        AssertUsername(apiVariables, "ConnectionStrings__tenancy", "decisya_tenancy");
        AssertUsername(apiVariables, "ConnectionStrings__entitlements", "decisya_entitlements");

        foreach (var ownerKey in OwnerCredentialEnvironmentKeysUsedElsewhereInTheAppHost)
        {
            apiVariables.Keys.Should().NotContain(ownerKey);
        }

        // decisya-migrator gets the owner connection (ConnectionStrings__decisya, injected by
        // WithReference(decisyaDb)) and the role password — and nothing named "tenancy".
        migratorVariables.Keys.Should().Contain("ConnectionStrings__decisya");
        migratorVariables.Keys.Should().Contain("Migrator__TenancyRolePassword");
        migratorVariables.Keys.Should().Contain("Migrator__EntitlementsRolePassword");
        migratorVariables.Keys.Should().NotContain("ConnectionStrings__tenancy");
        migratorVariables.Keys.Should().NotContain("ConnectionStrings__entitlements");
    }

    private static void AssertUsername(Dictionary<string, string> variables, string key, string expectedUsername)
    {
        var usernameMatch = Regex.Match(variables[key], "Username=([^;]*)");
        usernameMatch.Success.Should().BeTrue($"{key} should carry a Username segment");
        usernameMatch.Groups[1].Value.Should().Be(expectedUsername);
    }
}
