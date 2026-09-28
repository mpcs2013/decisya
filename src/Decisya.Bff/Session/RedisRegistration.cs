using System.Net;
using StackExchange.Redis;

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
            // #18 G6 F1: StackExchange.Redis puts the command and key (the session key) into
            // exception messages by default, and those reach the logs on every Redis failure.
            options.IncludeDetailInExceptions = false;

            // #87: on Windows, `localhost` resolves to ::1 before 127.0.0.1; the Redis
            // container only publishes its port on the IPv4 loopback, so the ::1 attempt is
            // refused and StackExchange.Redis's IPv6-then-IPv4 fallback burns ~2s past the 1s
            // ConnectTimeout above before it ever tries IPv4. Rewriting `localhost` to
            // 127.0.0.1 up front makes the first (and only) attempt the one that succeeds.
            RewriteLocalhostEndpoints(options);
        });
    }

    internal static void RewriteLocalhostEndpoints(ConfigurationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        for (var i = 0; i < options.EndPoints.Count; i++)
        {
            if (options.EndPoints[i] is DnsEndPoint dnsEndPoint
                && string.Equals(dnsEndPoint.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                options.EndPoints[i] = new IPEndPoint(IPAddress.Loopback, dnsEndPoint.Port);
            }
        }
    }
}
