namespace Decisya.Bff.Tests;

/// <summary>Builds a <see cref="BffWebApplicationFactory"/> wired to the shared
/// <see cref="KeycloakBffFixture"/> and <see cref="RedisFixture"/>, each test getting its own
/// throwaway Data Protection key-ring directory unless it needs to share one (the restart
/// test, NFR-23).</summary>
internal static class BffFactoryFactory
{
    internal static BffWebApplicationFactory Create(
        KeycloakBffFixture keycloakFixture, RedisFixture redisFixture, string? keyRingPath = null)
    {
        var path = keyRingPath ?? CreateTemporaryKeyRingDirectory();
        return EagerConfigurationGuard.BuildWithRedisConnectionString(
            redisFixture.ConnectionString,
            () => new BffWebApplicationFactory(keycloakFixture.Authority, keycloakFixture.ClientSecret, redisFixture.ConnectionString, path));
    }

    internal static string CreateTemporaryKeyRingDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "decisya-bff-tests-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
