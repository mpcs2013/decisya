using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Decisya.ServiceDefaults.Tests.Production;

/// <summary>Issue #120, D6: forwarded headers are honoured from the configured proxy only.</summary>
[Collection(RealAspNetCoreHostCollectionDefinition.Name)]
public class ForwardedHeadersTests
{
    private const string ClientAddress = "203.0.113.7";

    [Fact]
    public async Task Headers_from_the_configured_proxy_are_honoured()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(Environments.Production, "127.0.0.1", cancellationToken);

        var body = await PingAsync(host, cancellationToken);

        body.Should().Be($"{ClientAddress}|https");
    }

    [Fact]
    public async Task Headers_from_any_other_peer_are_ignored()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(Environments.Production, "10.120.0.2", cancellationToken);

        var body = await PingAsync(host, cancellationToken);

        body.Should().Be("127.0.0.1|http");
    }

    [Fact]
    public async Task With_no_proxy_configured_nobody_is_trusted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(Environments.Production, trustedProxies: null, cancellationToken);

        var body = await PingAsync(host, cancellationToken);

        body.Should().Be("127.0.0.1|http");
    }

    [Fact]
    public async Task In_Development_there_is_no_proxy_so_the_headers_are_ignored()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(Environments.Development, "127.0.0.1", cancellationToken);

        var body = await PingAsync(host, cancellationToken);

        body.Should().Be("127.0.0.1|http");
    }

    [Fact]
    public async Task Only_one_hop_is_taken_from_the_chain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(Environments.Production, "127.0.0.1", cancellationToken);

        using var client = ProductionTestHost.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(host.AppUri, "/ping"));
        request.Headers.Host = ProductionTestHost.AllowedHost;
        request.Headers.Add("X-Forwarded-For", "198.51.100.1, " + ClientAddress);
        using var response = await client.SendAsync(request, cancellationToken);

        // The last entry is the one the trusted proxy appended; the spoofed first one is not taken.
        (await response.Content.ReadAsStringAsync(cancellationToken)).Should().StartWith(ClientAddress);
    }

    [Theory]
    [InlineData("10.120.0.0/24")]
    [InlineData("*")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("not-an-address")]
    [InlineData("127.0.0.1,10.0.0.0/8")]
    [InlineData("255.255.255.255")]
    [InlineData("224.0.0.1")]
    public void A_range_wildcard_or_non_address_fails_start_up_naming_the_key(string value)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Configuration.AddInMemoryCollection([new(ProductionExtensions.TrustedProxiesKey, value)]);
        var app = builder.Build();

        var act = () => app.UseDecisyaForwardedHeaders();

        act.Should().Throw<InvalidOperationException>().WithMessage("*Decisya:Edge:TrustedProxies*");
    }

    [Theory]
    [InlineData("10.120.0.2", 1)]
    [InlineData("10.120.0.2, 10.120.0.3", 2)]
    [InlineData("10.120.0.2;::1", 2)]
    public void Single_unicast_addresses_are_accepted(string value, int count)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new(ProductionExtensions.TrustedProxiesKey, value)])
            .Build();

        ProductionExtensions.ReadTrustedProxies(configuration).Should().HaveCount(count);
    }

    private static async Task<string> PingAsync(ProductionTestHost host, CancellationToken cancellationToken)
    {
        using var client = ProductionTestHost.NewClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(host.AppUri, "/ping"));
        request.Headers.Host = ProductionTestHost.AllowedHost;
        request.Headers.Add("X-Forwarded-For", ClientAddress);
        request.Headers.Add("X-Forwarded-Proto", "https");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static Task<ProductionTestHost> StartAsync(string environment, string? trustedProxies, CancellationToken cancellationToken) =>
        ProductionTestHost.StartAsync(
            environment,
            new Dictionary<string, string?> { [ProductionExtensions.TrustedProxiesKey] = trustedProxies },
            beforeEndpoints: static app => app.UseDecisyaForwardedHeaders(),
            cancellationToken: cancellationToken);
}
