using System.Net.Http.Headers;
using System.Text.Json;
using Testcontainers.Keycloak;

namespace Decisya.Identity.Tests;

/// <summary>
/// Story 1 (the Done-when) and G4-17-20: starts its own container (never the shared
/// <see cref="KeycloakRealmFixture"/> instance, which every other Integration test in this
/// project reads), restarts it against the same import, and proves neither start fails and
/// the realm survives.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RealmReimportTests : IAsyncDisposable
{
    private KeycloakContainer? _container;

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_first_start_logs_the_realm_import_and_a_restart_keeps_the_realm_intact()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var clientSecret = KeycloakRealmFixture.GenerateHex(32);
        var devUserPassword = KeycloakRealmFixture.GenerateHex(20);
        var adminPassword = KeycloakRealmFixture.GenerateHex(24);

        _container = KeycloakRealmFixture.BuildContainer(clientSecret, devUserPassword, adminPassword);
        await _container.StartAsync(cancellationToken);

        var (stdout, stderr) = await _container.GetLogsAsync(ct: cancellationToken);
        var firstStartLogs = stdout + stderr;
        // G4 pins the observed 26.7.4 substring; either phrase covers a fresh admin
        // bootstrap or an existing-admin realm import (Testcontainers.Keycloak's own wait
        // strategy already waits for one of the two).
        (firstStartLogs.Contains("Imported realm", StringComparison.OrdinalIgnoreCase)
            || firstStartLogs.Contains("realm 'decisya'", StringComparison.OrdinalIgnoreCase)
            || firstStartLogs.Contains("Added user", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("the first start's own log should report the decisya realm import:\n" + firstStartLogs);

        await _container.StopAsync(cancellationToken);

        // G4 finding: Testcontainers.Keycloak's built-in wait strategy waits for a log line
        // ("Created temporary admin user..." or "Added user... to realm...") that is only
        // ever printed on a *fresh* bootstrap. On this restart, the realm and its admin user
        // already exist, so Keycloak (correctly) never prints either line again, and
        // StartAsync's re-applied wait strategy then blocks indefinitely. A restart boots in
        // seconds once the image's build/augmentation step has already run once (confirmed
        // manually), so this bounded timeout plus a direct, manual readiness poll below
        // reaches a working container without depending on that wait strategy at all.
        using var restartTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var restartLinked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, restartTimeout.Token);
        try
        {
            await _container.StartAsync(restartLinked.Token);
        }
        catch (Exception ex) when (restartTimeout.IsCancellationRequested
            && (ex is OperationCanceledException or TimeoutException))
        {
            // Expected per the note above: DotNet.Testcontainers' WaitStrategy surfaces the
            // bounded timeout as its own TimeoutException, not OperationCanceledException.
            // The container process itself is very likely already accepting connections;
            // the manual poll below proves it either way.
        }

        var baseAddress = _container.GetBaseAddress();
        var token = await PollForAdminTokenAsync(baseAddress, adminPassword, cancellationToken);

        using var client = new HttpClient { BaseAddress = new Uri(baseAddress) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var realmResponse = await client.GetAsync("/admin/realms/decisya", cancellationToken);
        realmResponse.EnsureSuccessStatusCode();

        using var clientsResponse = await client.GetAsync("/admin/realms/decisya/clients?clientId=decisya-bff", cancellationToken);
        using var clientsDocument = JsonDocument.Parse(await clientsResponse.Content.ReadAsStringAsync(cancellationToken));
        clientsDocument.RootElement.GetArrayLength().Should().Be(1, "the decisya-bff client should survive the restart");

        using var usersResponse = await client.GetAsync("/admin/realms/decisya/users?briefRepresentation=false", cancellationToken);
        using var usersDocument = JsonDocument.Parse(await usersResponse.Content.ReadAsStringAsync(cancellationToken));
        usersDocument.RootElement.GetArrayLength().Should().Be(3, "all three seeded users should survive the restart");
    }

    /// <summary>Retries the master-realm admin token request for up to 30s, so a slightly
    /// slow port/HTTP readiness window right after a restart fails the test with a clear
    /// timeout message instead of a single premature connection failure.</summary>
    private static async Task<string> PollForAdminTokenAsync(
        string baseAddress, string adminPassword, CancellationToken cancellationToken)
    {
        using var pollTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var pollLinked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, pollTimeout.Token);

        while (true)
        {
            try
            {
                return await KeycloakRealmFixture.GetAdminAccessTokenAsync(baseAddress, "admin", adminPassword, pollLinked.Token);
            }
            catch (Exception) when (!pollLinked.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), pollLinked.Token);
            }
        }
    }
}
