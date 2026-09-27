using System.Security.Cryptography;
using System.Text.Json;
using Decisya.AppHost;
using Testcontainers.Keycloak;

namespace Decisya.Identity.Tests;

/// <summary>
/// One throwaway Keycloak container, shared by every read-only <c>Category=Integration</c>
/// test class that only reads the imported <c>decisya</c> realm (G2). It always starts
/// with the same image the AppHost pins (<see cref="ContainerImages"/>, linked source) and
/// imports the same committed <c>deploy/keycloak/decisya-realm.json</c>.
/// </summary>
/// <remarks>
/// <see cref="RealmReimportTests"/> and the self-edit test in <see cref="TenantSelfEditTests"/>
/// start their own separate container instead of using this fixture, because a restart or a
/// user-attribute edit would disturb every other test sharing this instance (G2).
/// </remarks>
public sealed class KeycloakRealmFixture : IAsyncLifetime, IAsyncDisposable
{
    private const string AdminUsername = "admin";

    private readonly SemaphoreSlim _startLock = new(1, 1);

    private KeycloakContainer? _container;
    private string? _adminPassword;
    private bool _started;

    /// <summary>The value supplied to the container as <c>DECISYA_BFF_CLIENT_SECRET</c>.</summary>
    public string ClientSecret { get; private set; } = string.Empty;

    /// <summary>The value supplied to the container as <c>DECISYA_DEV_USER_PASSWORD</c>.</summary>
    public string DevUserPassword { get; private set; } = string.Empty;

    private KeycloakContainer Container =>
        _container ?? throw new InvalidOperationException(
            "KeycloakRealmFixture has not started yet. Call EnsureStartedAsync first.");

    /// <summary>The container's base HTTP address (host + mapped port), e.g. <c>http://localhost:32835</c>.</summary>
    public string BaseAddress => Container.GetBaseAddress();

    /// <summary>
    /// A true no-op. xunit.v3's <c>[assembly: AssemblyFixture&lt;T&gt;]</c> calls this
    /// unconditionally for every run, before trait filtering picks which test cases
    /// actually run. G6-01 (confirmed): resolving the two realm-placeholder secrets here
    /// — even though that resolution itself never touches Docker — made CI's unit step
    /// (<c>CI=true</c>, <c>--filter-not-trait "Category=Integration"</c>, and both
    /// variables unset by design, since they're exported only to the Integration step)
    /// throw "must be set in CI" for the whole assembly, failing every non-Docker test in
    /// it before the Integration step even ran. Resolution now happens lazily, in
    /// <see cref="EnsureStartedAsync"/>, alongside the container start it was always
    /// gated on — see that method's doc comment.
    /// </summary>
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        _startLock.Dispose();
    }

    /// <summary>
    /// Resolves the two realm-placeholder secrets (G3 "Changes to the G2 design" item 1;
    /// all three branches: a per-run fallback on the host, a naming failure when unset in
    /// CI, a failure everywhere when set but invalid — see <see cref="ResolveSecret"/>),
    /// then builds and starts the shared container. First call only; every later call is a
    /// no-op. Every <c>Category=Integration</c> test that reads this fixture must call this
    /// before using <see cref="BaseAddress"/>, <see cref="ClientSecret"/> or
    /// <see cref="DevUserPassword"/>. G6-01: doing this here, instead of in
    /// <see cref="InitializeAsync"/>, is what keeps it from running in CI's unit step.
    /// </summary>
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

            ClientSecret = ResolveSecret(
                "DECISYA_BFF_CLIENT_SECRET",
                RealmSecretRules.IsValidClientSecret,
                () => GenerateHex(RealmSecretRules.ClientSecretMinLength));
            DevUserPassword = ResolveSecret(
                "DECISYA_DEV_USER_PASSWORD",
                RealmSecretRules.IsValidDevPassword,
                () => GenerateHex(RealmSecretRules.DevPasswordMinLength));

            // The Testcontainers admin password is random for each run (G4-17-14); never
            // the library's own "admin" default.
            _adminPassword = GenerateHex(24);
            _container = BuildContainer(ClientSecret, DevUserPassword, _adminPassword);
            await _container.StartAsync(cancellationToken);
            _started = true;
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>
    /// Builds (but does not start) a Keycloak container that imports the committed realm
    /// export, wired the same way for every test in this project: the pinned image, no bind
    /// mount and no Docker socket (G4-17-18), and the two realm placeholders supplied as
    /// container environment.
    /// </summary>
    internal static KeycloakContainer BuildContainer(string clientSecret, string devUserPassword, string adminPassword)
    {
        var image = ContainerImages.Reference(
            ContainerImages.KeycloakRegistry, ContainerImages.KeycloakImage,
            ContainerImages.KeycloakTag, ContainerImages.KeycloakSha256);
        var realmFilePath = RepoPaths.Find(Path.Combine("deploy", "keycloak", "decisya-realm.json"));

        return new KeycloakBuilder(image)
            .WithUsername(AdminUsername)
            .WithPassword(adminPassword)
            .WithRealm(realmFilePath)
            .WithEnvironment("DECISYA_BFF_CLIENT_SECRET", clientSecret)
            .WithEnvironment("DECISYA_DEV_USER_PASSWORD", devUserPassword)
            .Build();
    }

    /// <summary>Requests a master-realm admin access token through <c>admin-cli</c>'s own
    /// password grant (never <c>decisya-bff</c>). Calls <see cref="EnsureStartedAsync"/>
    /// itself, so a test only needs this one call to reach a running container.</summary>
    public async Task<string> GetAdminAccessTokenAsync(CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);
        return await GetAdminAccessTokenAsync(
            BaseAddress,
            AdminUsername,
            _adminPassword ?? throw new InvalidOperationException("KeycloakRealmFixture has not started yet."),
            cancellationToken);
    }

    internal static async Task<string> GetAdminAccessTokenAsync(
        string baseAddress, string adminUsername, string adminPassword, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = new Uri(baseAddress) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/realms/master/protocol/openid-connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "admin-cli",
                ["username"] = adminUsername,
                ["password"] = adminPassword,
            }),
        };

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("The master admin token response had no access_token.");
    }

    /// <summary>
    /// G3 "Changes to the G2 design" item 1: a per-run fallback secret applies on the host
    /// only. When <c>CI</c> or <c>GITHUB_ACTIONS</c> is <c>true</c>, an unset variable fails
    /// the fixture instead, so CI proves the G1 wiring rather than silently generating a
    /// value nobody set (G4-17-02). <c>internal</c> so
    /// <see cref="KeycloakRealmFixtureSecretResolutionTests"/> calls it directly, with no
    /// container and no assembly-fixture lifecycle involved (G6-01).
    /// </summary>
    internal static string ResolveSecret(string variableName, Func<string?, bool> isValid, Func<string> generateFallback)
    {
        var value = Environment.GetEnvironmentVariable(variableName);

        if (string.IsNullOrEmpty(value))
        {
            if (IsRunningInCi())
            {
                throw new InvalidOperationException(
                    $"{variableName} must be set in CI (G3 'Changes to the G2 design', item 1).");
            }

            return generateFallback();
        }

        if (!isValid(value))
        {
            throw new InvalidOperationException($"{variableName} is set but does not satisfy RealmSecretRules.");
        }

        return value;
    }

    private static bool IsRunningInCi() =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

    internal static string GenerateHex(int length) =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(length))[..length];
}
