using Microsoft.AspNetCore.Mvc.Testing;

namespace Decisya.Bff.Tests;

/// <summary>
/// <c>Aspire.StackExchange.Redis</c>'s <c>AddRedisClient</c> reads and captures
/// <c>ConnectionStrings:redis</c> eagerly, synchronously, inside <c>Program.cs</c>'s own
/// top-level code — before <see cref="WebApplicationFactory{TEntryPoint}"/>'s
/// <c>ConfigureAppConfiguration</c> override (which only reaches the shared configuration
/// afterwards) has a chance to apply. Confirmed empirically: <c>IConfiguration.GetConnectionString("redis")</c>
/// reflects the override correctly, but the resulting <c>StackExchange.Redis.ConfigurationOptions</c>
/// still has zero endpoints.
/// </summary>
/// <remarks>
/// An environment variable, read by <c>WebApplication.CreateBuilder</c>'s own
/// <c>AddEnvironmentVariables()</c> call as one of the very first configuration sources, is
/// early enough. This guard sets it, forces the host to build (which is when the eager
/// capture happens), then clears it — serialized by a static lock so two tests can never see
/// each other's value, even under parallel test execution.
/// </remarks>
/// <remarks>
/// #19: <c>Program.cs</c> reads <c>Bff:Api:Address</c> the same way, directly off
/// <c>builder.Configuration</c>, to seed the YARP cluster's destination before <c>Build()</c> —
/// so the same fix applies: a test that needs the double's address (rather than
/// appsettings.json's own <c>https://decisya-api</c> default) must go through the same
/// environment-variable path, not <c>WebApplicationFactory{TEntryPoint}.ConfigureAppConfiguration</c>.
/// </remarks>
internal static class EagerConfigurationGuard
{
    private const string RedisConnectionStringVariable = "ConnectionStrings__redis";
    private const string ApiAddressVariable = "Bff__Api__Address";

    private static readonly SemaphoreSlim Lock = new(1, 1);

    internal static TFactory BuildWithRedisConnectionString<TFactory>(
        string redisConnectionString,
        Func<TFactory> buildFactory,
        string? apiAddress = null,
        IReadOnlyDictionary<string, string?>? eagerSettings = null)
        where TFactory : WebApplicationFactory<Program>
    {
        ArgumentNullException.ThrowIfNull(buildFactory);

        // #122: AddBffDataProtection reads Bff:DataProtection:* eagerly too; the same environment-variable path.
        var extra = (eagerSettings ?? new Dictionary<string, string?>())
            .Select(pair => (Name: pair.Key.Replace(":", "__", StringComparison.Ordinal), pair.Value))
            .ToList();

        Lock.Wait();
        try
        {
            foreach (var (name, value) in extra)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            Environment.SetEnvironmentVariable(RedisConnectionStringVariable, redisConnectionString);
            if (apiAddress is not null)
            {
                Environment.SetEnvironmentVariable(ApiAddressVariable, apiAddress);
            }

            var factory = buildFactory();
            _ = factory.Server; // forces the host to build now, while the variables are still set.
            return factory;
        }
        finally
        {
            foreach (var (name, _) in extra)
            {
                Environment.SetEnvironmentVariable(name, null);
            }

            Environment.SetEnvironmentVariable(RedisConnectionStringVariable, null);
            if (apiAddress is not null)
            {
                Environment.SetEnvironmentVariable(ApiAddressVariable, null);
            }

            Lock.Release();
        }
    }
}
