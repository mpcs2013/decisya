using System.Diagnostics.Metrics;
using System.Net;
using Decisya.Bff.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Decisya.Bff.Tests.RateLimiting.RateLimitRequests;

namespace Decisya.Bff.Tests.RateLimiting;

/// <summary>
/// #122 G3 G4-122-01 (a, b), NFR-51 and Story 5: the partition follows the trusted edge's forwarded
/// address and nothing else; a rejection is one event with a closed field set and a counter. No Docker.
/// </summary>
public class ForwardingAndLogTests
{
    private static readonly IPAddress Edge = IPAddress.Parse("10.120.0.2");

    private static List<CapturedLogRecord> Rejections(CapturingLoggerProvider provider) =>
        provider.Records.Where(r => r.EventId == 1820).ToList();

    [Fact]
    public async Task A_forged_forwarded_header_from_an_untrusted_peer_stays_in_the_peers_own_bucket()
    {
        using var factory = RateLimitFactory.Create(Limits(login: 2), edge: Edge);
        using var client = factory.CreateBffClient();

        (await StatusAsync(client, Get("/bff/login", peer: "6.6.6.6", forwardedFor: "198.51.100.1"))).Should().Be(HttpStatusCode.Redirect);
        (await StatusAsync(client, Get("/bff/login", peer: "6.6.6.6", forwardedFor: "198.51.100.2"))).Should().Be(HttpStatusCode.Redirect);
        (await StatusAsync(client, Get("/bff/login", peer: "6.6.6.6", forwardedFor: "198.51.100.3"))).Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task The_trusted_edge_selects_the_clients_bucket_and_the_log_says_ip()
    {
        var provider = new CapturingLoggerProvider();
        using var factory = RateLimitFactory.Create(Limits(login: 2), edge: Edge, loggerProvider: provider);
        using var client = factory.CreateBffClient();

        (await StatusAsync(client, Get("/bff/login", peer: "10.120.0.2", forwardedFor: "192.0.2.10"))).Should().Be(HttpStatusCode.Redirect);
        (await StatusAsync(client, Get("/bff/login", peer: "10.120.0.2", forwardedFor: "192.0.2.10"))).Should().Be(HttpStatusCode.Redirect);
        (await StatusAsync(client, Get("/bff/login", peer: "10.120.0.2", forwardedFor: "192.0.2.10"))).Should().Be(HttpStatusCode.TooManyRequests);
        (await StatusAsync(client, Get("/bff/login", peer: "10.120.0.2", forwardedFor: "192.0.2.11"))).Should().Be(HttpStatusCode.Redirect);

        var events = Rejections(provider);
        events.Should().ContainSingle();
        events[0].StateText.Should().Contain("PartitionKind=ip").And.Contain("RouteClass=login");
    }

    [Fact]
    public async Task A_request_from_the_edge_with_no_forwarded_address_is_unknown_and_visibly_so()
    {
        var provider = new CapturingLoggerProvider();
        using var factory = RateLimitFactory.Create(Limits(login: 1), edge: Edge, loggerProvider: provider);
        using var client = factory.CreateBffClient();

        (await StatusAsync(client, Get("/bff/login", peer: "10.120.0.2"))).Should().Be(HttpStatusCode.Redirect);
        (await StatusAsync(client, Get("/bff/login", peer: "10.120.0.2"))).Should().Be(HttpStatusCode.TooManyRequests);

        var events = Rejections(provider);
        events.Should().ContainSingle();
        events[0].StateText.Should().Contain("PartitionKind=unknown", "the T122-02 misconfiguration must show up as unknown, never as ip");
    }

    [Fact]
    public async Task No_address_at_all_is_one_shared_bucket_not_no_limit()
    {
        using var factory = RateLimitFactory.Create(Limits(login: 2));
        using var client = factory.CreateBffClient();

        (await StatusAsync(client, Get("/bff/login"))).Should().Be(HttpStatusCode.Redirect);
        (await StatusAsync(client, Get("/bff/login"))).Should().Be(HttpStatusCode.Redirect);
        (await StatusAsync(client, Get("/bff/login"))).Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_flood_writes_one_event_with_closed_fields_and_the_counter_counts_every_refusal()
    {
        var provider = new CapturingLoggerProvider();
        long counted = 0;
        var tagKeys = new HashSet<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Decisya.Bff" && instrument.Name == "decisya.bff.ratelimit.rejected")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            Interlocked.Add(ref counted, value);
            lock (tagKeys)
            {
                foreach (var tag in tags)
                {
                    tagKeys.Add(tag.Key + "=" + tag.Value);
                }
            }
        });
        listener.Start();

        var canaryQuery = Canaries.Unique("query");
        var canaryHeader = Canaries.Unique("xff");
        using var factory = RateLimitFactory.Create(Limits(login: 1), loggerProvider: provider);
        using var client = factory.CreateBffClient();

        (await StatusAsync(client, Get("/bff/login?returnUrl=/x&v=" + canaryQuery, peer: "198.51.100.77"))).Should().Be(HttpStatusCode.Redirect);
        for (var i = 0; i < 50; i++)
        {
            (await StatusAsync(client, Get("/bff/login?v=" + canaryQuery, peer: "198.51.100.77", forwardedFor: canaryHeader)))
                .Should().Be(HttpStatusCode.TooManyRequests);
        }

        var events = Rejections(provider);
        events.Should().ContainSingle("one event per partition per window");
        events[0].Level.Should().Be(LogLevel.Warning);
        events[0].EventName.Should().Be("ratelimit.rejected");
        events[0].StateText.Should().Contain("RouteClass=login").And.Contain("PartitionKind=ip");

        Interlocked.Read(ref counted).Should().Be(50);
        tagKeys.Should().BeEquivalentTo(["route_class=login"], "the counter carries route_class only");

        // The BFF's own records (the framework's request-starting line is outside this issue's control).
        provider.Records.Where(r => r.Category.StartsWith("Decisya.", StringComparison.Ordinal)).Should().NotContain(r =>
            r.Contains("198.51.100.77") || r.Contains(canaryQuery) || r.Contains(canaryHeader),
            "no address, query value or header value reaches a log record");
    }

    [Fact]
    public void Every_bff_and_api_endpoint_carries_a_route_class()
    {
        using var factory = RateLimitFactory.Create();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } raw
                && (raw.StartsWith("/bff", StringComparison.Ordinal) || raw.StartsWith("/api", StringComparison.Ordinal)))
            .ToList();

        endpoints.Select(e => e.RoutePattern.RawText).Should().Contain(["/bff/login", "/bff/me", "/bff/logout", "/bff/backchannel-logout", "/api/{**catch-all}"]);
        foreach (var endpoint in endpoints)
        {
            endpoint.Metadata.GetMetadata<RouteClassMetadata>().Should().NotBeNull(
                $"{endpoint.RoutePattern.RawText} needs a rate-limit class");
        }

        string ClassOf(string pattern) => endpoints.Single(e => e.RoutePattern.RawText == pattern)
            .Metadata.GetMetadata<RouteClassMetadata>()!.Class.ToString();

        ClassOf("/bff/login").Should().Be("Login");
        ClassOf("/bff/logout").Should().Be("Login");
        ClassOf("/bff/backchannel-logout").Should().Be("BackchannelLogout");
        ClassOf("/bff/me").Should().Be("Api");
        ClassOf("/api/{**catch-all}").Should().Be("Api");
    }
}
