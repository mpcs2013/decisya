namespace Decisya.Bff.Session;

/// <summary>
/// Keeps the direct <c>StackExchange.Redis</c> reference (the <c>configureOptions</c>
/// delegate's parameter type) inside <c>Decisya.Bff.Session</c> (the NetArchTest rule, G2),
/// even though <c>Program.cs</c> is where this is called from.
/// </summary>
internal static class RedisRegistration
{
    /// <summary>G2's fail-closed Redis client wiring (NFR-22): a short connect/sync/async
    /// timeout and no abort-on-connect-fail, so <see cref="RedisTicketStore"/>'s own bounded
    /// waits are the only thing a caller ever blocks on.</summary>
    internal static void AddBffRedisClient(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddRedisClient("redis", configureOptions: options =>
        {
            options.AbortOnConnectFail = false;
            options.ConnectTimeout = 1000;
            options.SyncTimeout = 1000;
            options.AsyncTimeout = 1000;
        });
    }
}
