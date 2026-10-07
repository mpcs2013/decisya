using System.Net;
using Decisya.Bff.Session;
using StackExchange.Redis;

namespace Decisya.Bff.Tests;

/// <summary>
/// #87: on Windows, <c>localhost</c> resolves to <c>::1</c> before <c>127.0.0.1</c>; the Redis
/// container only publishes on the IPv4 loopback, so the refused <c>::1</c> attempt eats the
/// whole <see cref="RedisRegistration"/> 1s <c>ConnectTimeout</c> before the IPv4 fallback ever
/// runs. This is a pure data-structure test (no Redis, no network): it only exercises
/// <see cref="RedisRegistration.RewriteLocalhostEndpoints"/> against a <see cref="ConfigurationOptions"/>.
/// </summary>
public class RedisLocalhostEndpointRewriteTests
{
    [Fact]
    public void Localhost_endpoint_is_rewritten_to_the_IPv4_loopback()
    {
        var options = ConfigurationOptions.Parse("localhost:6379");

        RedisRegistration.RewriteLocalhostEndpoints(options);

        options.EndPoints.Should().ContainSingle().Which.Should().Be(new IPEndPoint(IPAddress.Loopback, 6379));
    }

    [Fact]
    public void Localhost_rewrite_is_case_insensitive()
    {
        var options = ConfigurationOptions.Parse("LOCALHOST:6379");

        RedisRegistration.RewriteLocalhostEndpoints(options);

        options.EndPoints.Should().ContainSingle().Which.Should().Be(new IPEndPoint(IPAddress.Loopback, 6379));
    }

    [Fact]
    public void A_non_localhost_host_is_left_unchanged()
    {
        var options = ConfigurationOptions.Parse("redis.internal:6379");

        RedisRegistration.RewriteLocalhostEndpoints(options);

        options.EndPoints.Should().ContainSingle().Which.Should().Be(new DnsEndPoint("redis.internal", 6379));
    }

    [Fact]
    public void An_IP_endpoint_is_left_unchanged()
    {
        var options = ConfigurationOptions.Parse("192.0.2.5:6379");

        RedisRegistration.RewriteLocalhostEndpoints(options);

        options.EndPoints.Should().ContainSingle().Which.Should().Be(new IPEndPoint(IPAddress.Parse("192.0.2.5"), 6379));
    }
}
