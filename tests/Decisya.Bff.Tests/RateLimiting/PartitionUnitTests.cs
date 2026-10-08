using System.Net;
using System.Threading.RateLimiting;
using Decisya.Bff.RateLimiting;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using NodaTime;
using NodaTime.Testing;

namespace Decisya.Bff.Tests.RateLimiting;

/// <summary>#122 G3 G4-122-01 a, c, d and NFR-51: the pure partition, classifier and limiter rules.</summary>
public class PartitionUnitTests
{
    private static readonly IPAddress Edge = IPAddress.Parse("10.120.0.2");

    private static (PartitionKind Kind, string Value) Partition(string? address, params IPAddress[] trusted) =>
        ClientPartition.FromAddress(address is null ? null : IPAddress.Parse(address), trusted);

    [Fact]
    public void A_client_address_is_an_ip_partition()
    {
        Partition("192.0.2.7", Edge).Should().Be((PartitionKind.Ip, "v4:192.0.2.7"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void A_missing_or_unspecified_address_is_the_shared_unknown_bucket(string? address)
    {
        Partition(address, Edge).Kind.Should().Be(PartitionKind.Unknown);
    }

    [Theory]
    [InlineData("10.120.0.2")]
    [InlineData("::ffff:10.120.0.2")]
    public void An_address_equal_to_a_trusted_proxy_means_forwarding_did_not_apply_and_is_unknown(string address)
    {
        Partition(address, Edge).Should().Be((PartitionKind.Unknown, "-"));
    }

    [Fact]
    public void Addresses_in_one_IPv6_slash_64_share_a_partition_and_other_prefixes_do_not()
    {
        var a = Partition("2001:db8:1:2:aaaa:bbbb:cccc:dddd");
        var b = Partition("2001:db8:1:2:1:2:3:4");
        var c = Partition("2001:db8:1:3:1:2:3:4");

        a.Kind.Should().Be(PartitionKind.Ip);
        a.Should().Be(b);
        c.Value.Should().NotBe(a.Value);
    }

    [Fact]
    public void An_IPv4_mapped_IPv6_address_is_the_same_partition_as_the_IPv4_address()
    {
        Partition("::ffff:192.0.2.7").Should().Be(Partition("192.0.2.7"));
    }

    [Fact]
    public void The_partition_code_reads_no_request_header()
    {
        var source = File.ReadAllText(RepoPaths.Find(Path.Combine("src", "Decisya.Bff", "RateLimiting", "ClientPartition.cs")));
        source.Should().NotContain("Request.Headers").And.NotContain("GetTypedHeaders").And.NotContain("Headers[").And.NotContain("\"X-Forwarded");
    }

    private static OpenIdConnectOptions Oidc() => new()
    {
        CallbackPath = "/signin-oidc",
        SignedOutCallbackPath = "/signout-callback-oidc",
        RemoteSignOutPath = PathString.Empty,
    };

    private static RouteClass? Classify(string path, RouteClass? metadata = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (metadata is not null)
        {
            context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new RouteClassMetadata(metadata.Value)), "test"));
        }

        return RouteClassifier.Classify(context, Oidc());
    }

    [Theory]
    [InlineData("/signin-oidc")]
    [InlineData("/SIGNIN-OIDC")]
    [InlineData("/Signout-Callback-Oidc")]
    public void The_OIDC_handler_paths_are_login_whatever_their_case(string path)
    {
        Classify(path).Should().Be(RouteClass.Login);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/signin-oidc/x")]
    [InlineData("/dashboard")]
    [InlineData("/assets/app.js")]
    [InlineData("/health")]
    public void Other_paths_have_no_class(string path)
    {
        Classify(path).Should().BeNull();
    }

    [Theory]
    [InlineData("/api/admin/tenants")]
    [InlineData("/api/admin")]
    [InlineData("/API/ADMIN/x")]
    [InlineData("//api//admin/x")]
    [InlineData("/api/%61dmin/x")]
    [InlineData("/api/%2561dmin/x")]
    [InlineData("/api/./admin/x")]
    [InlineData("/api\\admin/x")]
    public void Admin_path_forms_are_the_admin_class(string path)
    {
        Classify(path, RouteClass.Api).Should().Be(RouteClass.Admin);
    }

    [Theory]
    [InlineData("/api/administrator")]
    [InlineData("/api/tenancy/me")]
    [InlineData("/api/x/admin")]
    [InlineData("/api")]
    public void Other_api_paths_stay_in_the_api_class(string path)
    {
        Classify(path, RouteClass.Api).Should().Be(RouteClass.Api);
    }

    [Theory]
    [InlineData("/bff/unmapped")]
    [InlineData("/api/unmapped")]
    public void An_unmapped_bff_or_api_path_is_limited_as_api_never_unlimited(string path)
    {
        Classify(path).Should().Be(RouteClass.Api);
    }

    [Theory]
    [InlineData(60, 4, 15)]
    [InlineData(60, 6, 10)]
    [InlineData(1, 4, 1)]
    [InlineData(5, 6, 1)]
    [InlineData(3600, 4, 900)]
    public void Retry_after_is_the_computed_window_over_segments_clamped(int window, int segments, int expected)
    {
        PartitionLimiter.ComputeRetryAfterSeconds(window, segments).Should().Be(expected);
    }

    private static PartitionLimiter NewLimiter(IClock clock, RateLimitPartitionStats stats) =>
        new(RouteClass.Login, PartitionKind.Ip, permitLimit: 1, windowSeconds: 60, segments: 4, clock, stats);

    [Fact]
    public void A_refusal_is_flagged_for_logging_once_per_window()
    {
        var clock = new FakeClock(Instant.FromUtc(2026, 10, 8, 12, 0));
        var stats = new RateLimitPartitionStats();
        using var limiter = NewLimiter(clock, stats);

        using var accepted = limiter.AttemptAcquire();
        accepted.IsAcquired.Should().BeTrue();

        bool First()
        {
            using var lease = limiter.AttemptAcquire();
            lease.IsAcquired.Should().BeFalse();
            lease.TryGetMetadata(PartitionLimiter.RejectionMetadata, out var rejection).Should().BeTrue();
            lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter).Should().BeTrue();
            retryAfter.Should().Be(TimeSpan.FromSeconds(15));
            return rejection!.ClaimLog();
        }

        First().Should().BeTrue();
        First().Should().BeFalse();
        First().Should().BeFalse();
        clock.AdvanceSeconds(61);
        First().Should().BeTrue("a new window logs again");
    }

    [Fact]
    public async Task Disposing_synchronously_or_asynchronously_releases_the_tracked_count_once()
    {
        var stats = new RateLimitPartitionStats();
        var clock = new FakeClock(Instant.FromUtc(2026, 10, 8, 12, 0));
        var first = NewLimiter(clock, stats);
        var second = NewLimiter(clock, stats);
        stats.TrackedPartitions.Should().Be(2);

        first.Dispose();
        first.Dispose();
        await second.DisposeAsync();
        stats.TrackedPartitions.Should().Be(0);
    }

    [Fact]
    public async Task Idle_partitions_are_evicted_by_the_real_heartbeat_after_20000_single_use_addresses()
    {
        var options = new BffRateLimitOptions();
        options.Login.PermitLimit = 1;
        options.Login.WindowSeconds = 1;
        var stats = new RateLimitPartitionStats();
        using var limiter = BffRateLimiterFactory.Create(options, SystemClock.Instance, stats);

        for (var i = 0; i < 20_000; i++)
        {
            var context = new DefaultHttpContext();
            context.Features.Set(new RateLimitPartitionFeature(RouteClass.Login, PartitionKind.Ip, "v4:" + i, null));
            using var lease = limiter.AttemptAcquire(context);
            lease.IsAcquired.Should().BeTrue();
        }

        stats.TrackedPartitions.Should().BeGreaterThan(1000);
        var deadline = Environment.TickCount64 + 60_000;
        while (stats.TrackedPartitions >= 1000 && Environment.TickCount64 < deadline)
        {
            await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        stats.TrackedPartitions.Should().BeLessThan(1000);
    }

    private static (BffRateLimitOptions Options, Microsoft.Extensions.Options.ValidateOptionsResult Result) Bind(
        Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var options = new BffRateLimitOptions();
        options.Load(configuration.GetSection(BffRateLimitOptions.SectionName));
        return (options, new BffRateLimitOptionsValidator().Validate(null, options));
    }

    [Fact]
    public void Defaults_apply_when_nothing_is_configured()
    {
        var (options, result) = Bind([]);

        result.Succeeded.Should().BeTrue();
        (options.Login.PermitLimit, options.BackchannelLogout.PermitLimit, options.Api.PermitLimit, options.Api.AnonymousPermitLimit, options.Admin.PermitLimit)
            .Should().Be((10, 300, 300, 60, 30));
        new[] { options.Login, options.BackchannelLogout, options.Api, options.Admin }
            .Should().OnlyContain(limit => limit.WindowSeconds == 60);
    }

    [Theory]
    [InlineData("Bff:RateLimits:login:PermitLimit", "0")]
    [InlineData("Bff:RateLimits:api:WindowSeconds", "-5")]
    [InlineData("Bff:RateLimits:admin:PermitLimit", "abc")]
    [InlineData("Bff:RateLimits:api:AnonymousPermitLimit", "100001")]
    [InlineData("Bff:RateLimits:backchannel_logout:WindowSeconds", "3601")]
    [InlineData("Bff:RateLimits:bogus:PermitLimit", "5")]
    [InlineData("Bff:RateLimits:login:PermitLimt", "5")]
    [InlineData("Bff:RateLimits:login:AnonymousPermitLimit", "5")]
    public void An_invalid_or_unknown_setting_fails_validation_naming_the_key_and_never_the_value(string key, string value)
    {
        var (_, result) = Bind(new() { [key] = value });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(key.Contains("bogus", StringComparison.Ordinal) ? "Bff:RateLimits:bogus" : key);
        if (value.Length > 1)
        {
            result.FailureMessage.Should().NotContain(value);
        }
    }

    [Theory]
    [InlineData("Bff:RateLimits:login:PermitLimit", "0")]
    [InlineData("Bff:RateLimits:bogus:PermitLimit", "5")]
    public void An_invalid_limit_stops_the_host_start_in_every_environment(string key, string value)
    {
        using var factory = new FailingStartFactory(new() { [key] = value });

        var failure = Record.Exception(() => factory.Server);

        failure.Should().NotBeNull();
        var message = Flatten(failure!);
        message.Should().Contain(key.Contains("bogus", StringComparison.Ordinal) ? "Bff:RateLimits:bogus" : key);
    }

    private static string Flatten(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            parts.Add(current.Message);
        }

        return string.Join(" | ", parts);
    }

    private sealed class FailingStartFactory(Dictionary<string, string?> overrides)
        : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = TestConfiguration.GoodOverrides();
                foreach (var pair in overrides)
                {
                    values[pair.Key] = pair.Value;
                }

                configuration.AddInMemoryCollection(values);
            });
        }
    }
}
