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
internal static class EagerConfigurationGuard
{
    private const string RedisConnectionStringVariable = "ConnectionStrings__redis";

    private static readonly SemaphoreSlim Lock = new(1, 1);

    internal static TFactory BuildWithRedisConnectionString<TFactory>(string redisConnectionString, Func<TFactory> buildFactory)
        where TFactory : WebApplicationFactory<Program>
    {
        ArgumentNullException.ThrowIfNull(buildFactory);

        Lock.Wait();
        try
        {
            Environment.SetEnvironmentVariable(RedisConnectionStringVariable, redisConnectionString);
            var factory = buildFactory();
            _ = factory.Server; // forces the host to build now, while the variable is still set.
            return factory;
        }
        finally
        {
            Environment.SetEnvironmentVariable(RedisConnectionStringVariable, null);
            Lock.Release();
        }
    }
}
