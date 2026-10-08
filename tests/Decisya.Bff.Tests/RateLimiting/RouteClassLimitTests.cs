using System.Net;
using System.Text.Json;
using Decisya.Bff.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using static Decisya.Bff.Tests.RateLimiting.RateLimitRequests;

namespace Decisya.Bff.Tests.RateLimiting;

/// <summary>
/// #122 NFR-49 and NFR-50: each route class returns 429 past its limit, with the generic problem and a
/// whole-second Retry-After. Short real windows (the framework limiters have no clock hook, spike R4).
/// No Docker: only anonymous callers, so no request reaches Redis, Keycloak or the Api.
/// </summary>
public class RouteClassLimitTests
{
    private static readonly string[] AllowedProblemKeys = ["type", "title", "status", "traceId"];

    [Fact]
    public async Task Login_class_returns_429_past_its_limit_and_another_address_is_not_affected()
    {
        using var factory = RateLimitFactory.Create(Limits(login: 3));
        using var client = factory.CreateBffClient();

        for (var i = 0; i < 3; i++)
        {
            (await StatusAsync(client, Get("/bff/login", peer: "198.51.100.11"))).Should().Be(HttpStatusCode.Redirect);
        }

        using var refused = await client.SendAsync(Get("/bff/login", peer: "198.51.100.11"), TestContext.Current.CancellationToken);
        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        refused.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        refused.Headers.GetValues("Retry-After").Single().Should().Be("15", "ceil(60 s / 4 segments), the computed value (spike R1)");
        refused.Headers.CacheControl!.NoStore.Should().BeTrue();
        refused.Headers.Contains("Set-Cookie").Should().BeFalse("the endpoint never ran: no correlation cookie, no session cookie");

        (await StatusAsync(client, Get("/bff/login", peer: "198.51.100.12"))).Should().Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Backchannel_logout_class_returns_429_past_its_limit_and_is_independent_of_login()
    {
        using var factory = RateLimitFactory.Create(Limits(login: 1, backchannel: 2));
        using var client = factory.CreateBffClient();

        (await StatusAsync(client, Get("/bff/login", peer: "198.51.100.21"))).Should().Be(HttpStatusCode.Redirect);
        (await StatusAsync(client, Get("/bff/login", peer: "198.51.100.21"))).Should().Be(HttpStatusCode.TooManyRequests);

        for (var i = 0; i < 2; i++)
        {
            (await StatusAsync(client, PostForm("/bff/backchannel-logout", new() { ["logout_token"] = "a.b.c" }, "198.51.100.21")))
                .Should().Be(HttpStatusCode.BadRequest, "the login class being exhausted does not touch this class");
        }

        (await StatusAsync(client, PostForm("/bff/backchannel-logout", new() { ["logout_token"] = "a.b.c" }, "198.51.100.21")))
            .Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Api_class_returns_429_not_401_past_the_anonymous_limit()
    {
        using var factory = RateLimitFactory.Create(Limits(anonymous: 3));
        using var client = factory.CreateBffClient();

        for (var i = 0; i < 3; i++)
        {
            (await StatusAsync(client, Get("/api/anything", peer: "198.51.100.31"))).Should().Be(HttpStatusCode.Unauthorized);
        }

        (await StatusAsync(client, Get("/api/anything", peer: "198.51.100.31"))).Should().Be(
            HttpStatusCode.TooManyRequests, "the limiter runs before authorization");
        (await StatusAsync(client, Get("/api/anything", peer: "198.51.100.32"))).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_endpoint_belongs_to_the_api_class()
    {
        using var factory = RateLimitFactory.Create(Limits(anonymous: 2));
        using var client = factory.CreateBffClient();

        (await StatusAsync(client, Get("/bff/me", peer: "198.51.100.41"))).Should().Be(HttpStatusCode.OK);
        (await StatusAsync(client, Get("/bff/me", peer: "198.51.100.41"))).Should().Be(HttpStatusCode.OK);
        (await StatusAsync(client, Get("/bff/me", peer: "198.51.100.41"))).Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Admin_class_returns_429_past_its_own_limit()
    {
        using var factory = RateLimitFactory.Create(Limits(admin: 2, anonymous: 100));
        using var client = factory.CreateBffClient();

        (await StatusAsync(client, Get("/api/admin/tenants", peer: "198.51.100.51"))).Should().Be(HttpStatusCode.Unauthorized);
        (await StatusAsync(client, Get("/api/admin/tenants", peer: "198.51.100.51"))).Should().Be(HttpStatusCode.Unauthorized);
        (await StatusAsync(client, Get("/api/admin/tenants", peer: "198.51.100.51"))).Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Admin_requests_also_count_against_the_api_class()
    {
        using var factory = RateLimitFactory.Create(Limits(admin: 100, anonymous: 3));
        using var client = factory.CreateBffClient();

        (await StatusAsync(client, Get("/api/admin/tenants", peer: "198.51.100.61"))).Should().Be(HttpStatusCode.Unauthorized);
        (await StatusAsync(client, Get("/API/ADMIN/tenants", peer: "198.51.100.61"))).Should().Be(HttpStatusCode.Unauthorized);
        (await StatusAsync(client, Get("/api/other", peer: "198.51.100.61"))).Should().Be(HttpStatusCode.Unauthorized);

        (await StatusAsync(client, Get("/api/admin/tenants", peer: "198.51.100.61"))).Should().Be(
            HttpStatusCode.TooManyRequests, "two admin requests spent two api permits: the tighter bucket decides");
    }

    [Theory]
    [InlineData("/signin-oidc")]
    [InlineData("/SIGNIN-OIDC")]
    [InlineData("/Signout-Callback-Oidc")]
    public async Task The_OIDC_callback_paths_are_the_login_class_whatever_their_case(string path)
    {
        using var factory = RateLimitFactory.Create(Limits(login: 2));
        using var client = factory.CreateBffClient();
        var peer = "198.51.100.71";

        (await StatusAsync(client, Get("/signin-oidc", peer))).Should().NotBe(HttpStatusCode.TooManyRequests);
        (await StatusAsync(client, Get("/Signout-Callback-Oidc", peer))).Should().NotBe(HttpStatusCode.TooManyRequests);
        (await StatusAsync(client, Get(path, peer))).Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Every_class_returns_the_same_generic_problem_that_names_nothing()
    {
        using var factory = RateLimitFactory.Create(Limits(login: 1, backchannel: 1, anonymous: 1, admin: 1));
        using var client = factory.CreateBffClient();
        const string peer = "198.51.100.88";

        var bodies = new List<Dictionary<string, JsonElement>>();
        async Task Collect(Func<HttpRequestMessage> make)
        {
            (await StatusAsync(client, make())).Should().NotBe(HttpStatusCode.TooManyRequests);
            using var response = await client.SendAsync(make(), TestContext.Current.CancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

            var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            var headers = string.Join('\n', response.Headers.Select(h => h.Key + "=" + string.Join(',', h.Value)));
            foreach (var secret in new[] { peer, "login", "backchannel", "admin", "PermitLimit", "session", "unknown" })
            {
                text.Should().NotContainEquivalentOf(secret);
                headers.Should().NotContainEquivalentOf(secret);
            }

            var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text)!;
            body.Keys.Should().BeSubsetOf(AllowedProblemKeys);
            body["status"].GetInt32().Should().Be(429);
            body["title"].GetString().Should().Be("Too many requests");
            body.ContainsKey("traceId").Should().BeTrue();
            body.Remove("traceId");
            bodies.Add(body);

            int.Parse(response.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture)
                .Should().BeInRange(1, 60);
        }

        await Collect(() => Get("/bff/login", peer));
        await Collect(() => PostForm("/bff/backchannel-logout", new() { ["logout_token"] = "a.b.c" }, peer));
        await Collect(() => Get("/api/x", peer));
        await Collect(() => Get("/api/admin/x", "198.51.100.89"));

        bodies.Skip(1).Should().OnlyContain(body => body.Count == bodies[0].Count
            && body.All(pair => bodies[0][pair.Key].GetRawText() == pair.Value.GetRawText()),
            "the bodies differ only in traceId");
    }

    [Fact]
    public async Task Static_assets_and_the_shell_are_not_limited()
    {
        using var factory = RateLimitFactory.Create(Limits(login: 1, anonymous: 1));
        using var client = factory.CreateBffClient();

        for (var i = 0; i < 5; i++)
        {
            (await StatusAsync(client, Get("/dashboard", peer: "198.51.100.91"))).Should().NotBe(HttpStatusCode.TooManyRequests);
            (await StatusAsync(client, Get("/assets/app.js", peer: "198.51.100.91"))).Should().NotBe(HttpStatusCode.TooManyRequests);
        }
    }

    [Fact]
    public async Task A_refused_partition_recovers_after_its_window_through_the_real_heartbeat_and_state_is_tracked()
    {
        using var factory = RateLimitFactory.Create(Limits(login: 2, windowSeconds: 5));
        using var client = factory.CreateBffClient();
        var stats = factory.Services.GetRequiredService<RateLimitPartitionStats>();

        // The first request through a new host is slow (the Redis client's connect attempt); warm it up on
        // another partition so the window under test is not spent waiting.
        (await StatusAsync(client, Get("/bff/login", peer: "198.51.100.250"))).Should().Be(HttpStatusCode.Redirect);

        (await StatusAsync(client, Get("/bff/login", peer: "198.51.100.101"))).Should().Be(HttpStatusCode.Redirect);
        (await StatusAsync(client, Get("/bff/login", peer: "198.51.100.101"))).Should().Be(HttpStatusCode.Redirect);
        (await StatusAsync(client, Get("/bff/login", peer: "198.51.100.101"))).Should().Be(HttpStatusCode.TooManyRequests);
        stats.TrackedPartitions.Should().BeGreaterThan(0);

        var deadline = Environment.TickCount64 + 12_000;
        var recovered = false;
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(250, TestContext.Current.CancellationToken);
            if (await StatusAsync(client, Get("/bff/login", peer: "198.51.100.101")) == HttpStatusCode.Redirect)
            {
                recovered = true;
                break;
            }
        }

        recovered.Should().BeTrue("the wrapper forwards TryReplenish, so the heartbeat replenishes it");
    }
}
