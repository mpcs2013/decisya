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
        await _container.StartAsync(cancellationToken);

        var baseAddress = _container.GetBaseAddress();
        var token = await KeycloakRealmFixture.GetAdminAccessTokenAsync(baseAddress, "admin", adminPassword, cancellationToken);

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
}
