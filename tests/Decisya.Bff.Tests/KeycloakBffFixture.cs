using System.Security.Cryptography;
using Decisya.AppHost;
using Testcontainers.Keycloak;

namespace Decisya.Bff.Tests;

/// <summary>
/// One throwaway Keycloak container per test run, shared by every <c>Category=Integration</c>
/// class that needs a real login (mirrors <c>Decisya.Identity.Tests/KeycloakRealmFixture</c>,
/// lazy start per the testcontainers skill: containers and secrets are resolved only in
/// <see cref="EnsureStartedAsync"/>, never in <see cref="InitializeAsync"/>, which xUnit v3
/// runs unconditionally even for the unit lane).
/// </summary>
public sealed class KeycloakBffFixture : IAsyncLifetime, IAsyncDisposable
{
    private readonly SemaphoreSlim _startLock = new(1, 1);

    private KeycloakContainer? _container;
    private bool _started;

    /// <summary>The value supplied to the container as <c>DECISYA_BFF_CLIENT_SECRET</c>.</summary>
    public string ClientSecret { get; private set; } = string.Empty;

    /// <summary>The value supplied to the container as <c>DECISYA_DEV_USER_PASSWORD</c>.</summary>
    public string DevUserPassword { get; private set; } = string.Empty;

    /// <summary>The container's base HTTP address, e.g. <c>http://localhost:32835</c>.</summary>
    public string BaseAddress => (_container ?? throw NotStarted()).GetBaseAddress();

    /// <summary>The realm issuer this container serves, e.g. <c>http://localhost:32835/realms/decisya</c>.</summary>
    public string Authority => $"{BaseAddress.TrimEnd('/')}/realms/decisya";

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        _startLock.Dispose();
    }

    public async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return;
        }

        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_started)
            {
                return;
            }

            ClientSecret = TestSecrets.ResolveOrFail("DECISYA_BFF_CLIENT_SECRET");
            DevUserPassword = TestSecrets.ResolveOrFail("DECISYA_DEV_USER_PASSWORD");
            var adminPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

            var image = ContainerImages.Reference(
                ContainerImages.KeycloakRegistry, ContainerImages.KeycloakImage,
                ContainerImages.KeycloakTag, ContainerImages.KeycloakSha256);
            var realmFilePath = RepoPaths.Find(Path.Combine("deploy", "keycloak", "decisya-realm.json"));

            _container = new KeycloakBuilder(image)
                .WithUsername("admin")
                .WithPassword(adminPassword)
                .WithRealm(realmFilePath)
                .WithEnvironment("DECISYA_BFF_CLIENT_SECRET", ClientSecret)
                .WithEnvironment("DECISYA_DEV_USER_PASSWORD", DevUserPassword)
                .Build();

            await _container.StartAsync(cancellationToken);
            _started = true;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private static InvalidOperationException NotStarted() =>
        new("KeycloakBffFixture has not started yet. Call EnsureStartedAsync first.");
}
