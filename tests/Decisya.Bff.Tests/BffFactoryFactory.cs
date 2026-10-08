using Microsoft.Extensions.Logging;
using NodaTime;

namespace Decisya.Bff.Tests;

/// <summary>Builds a <see cref="BffWebApplicationFactory"/> wired to the shared
/// <see cref="KeycloakBffFixture"/> and <see cref="RedisFixture"/>, each test getting its own
/// throwaway Data Protection key-ring directory unless it needs to share one (the restart
/// test, NFR-23).</summary>
internal static class BffFactoryFactory
{
    internal static BffWebApplicationFactory Create(
        KeycloakBffFixture keycloakFixture,
        RedisFixture redisFixture,
        string? keyRingPath = null,
        string? redisConnectionString = null,
        string? apiAddress = null,
        IClock? clock = null,
        HttpMessageHandler? backchannelHttpHandler = null,
        ILoggerProvider? loggerProvider = null)
    {
        var path = keyRingPath ?? CreateTemporaryKeyRingDirectory();
        var connectionString = redisConnectionString ?? redisFixture.ConnectionString;
        return EagerConfigurationGuard.BuildWithRedisConnectionString(
            connectionString,
            () => new BffWebApplicationFactory(
                keycloakFixture.Authority,
                keycloakFixture.ClientSecret,
                connectionString,
                path,
                apiAddress: apiAddress,
                clock: clock,
                backchannelHttpHandler: backchannelHttpHandler,
                loggerProvider: loggerProvider),
            apiAddress: apiAddress);
    }

    internal static string CreateTemporaryKeyRingDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "decisya-bff-tests-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}
