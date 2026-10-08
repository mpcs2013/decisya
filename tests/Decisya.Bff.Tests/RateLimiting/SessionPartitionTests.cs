using System.Net;
using System.Security.Claims;
using Decisya.Bff.Session;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodaTime;
using static Decisya.Bff.Tests.RateLimiting.RateLimitRequests;

namespace Decisya.Bff.Tests.RateLimiting;

/// <summary>
/// #122 Stories 3 and 4 with real sessions: the partition is the session, not the address, and a refused
/// request never reaches the Api. Real Redis (Testcontainers); the Api is the loopback ApiDouble.
/// </summary>
[Trait("Category", "Integration")]
public class SessionPartitionTests(RedisFixture redisFixture)
{
    private const string SessionIdClaim = "Microsoft.AspNetCore.Authentication.Cookies-SessionId";

    private static async Task<string> SeedSessionAsync(RateLimitFactory factory, string tenant)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sid", Canaries.Unique("sid")), new Claim("sub", Canaries.Unique("sub")), new Claim("tenant_id", tenant)],
            "TestSeed"));
        var now = NodaTime.SystemClock.Instance.GetCurrentInstant();
        var properties = new AuthenticationProperties { ExpiresUtc = now.Plus(Duration.FromHours(1)).ToDateTimeOffset() };
        properties.StoreTokens(
        [
            new AuthenticationToken { Name = "access_token", Value = Canaries.Unique("access") },
            new AuthenticationToken { Name = "expires_at", Value = now.Plus(Duration.FromHours(1)).ToDateTimeOffset().ToString("o", System.Globalization.CultureInfo.InvariantCulture) },
        ]);

        var key = await factory.Services.GetRequiredService<RedisTicketStore>()
            .StoreAsync(new AuthenticationTicket(principal, properties, "Cookies"));

        var sessionTicket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(SessionIdClaim, key)], "Cookies")),
            new AuthenticationProperties { ExpiresUtc = now.Plus(Duration.FromHours(1)).ToDateTimeOffset() },
            "Cookies");
        var cookieOptions = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        return "__Host-decisya-session=" + cookieOptions.TicketDataFormat.Protect(sessionTicket);
    }

    [Fact]
    public async Task The_api_class_counts_per_session_and_a_refused_request_never_reaches_the_api()
    {
        var ct = TestContext.Current.CancellationToken;
        await redisFixture.EnsureStartedAsync(ct);
        await using var api = await ApiDouble.StartAsync(ct);
        var provider = new CapturingLoggerProvider();
        using var factory = RateLimitFactory.Create(
            Limits(api: 3), loggerProvider: provider, redisConnectionString: redisFixture.ConnectionString, apiAddress: api.Address);
        using var client = factory.CreateBffClient();
        provider.Enrichment = factory.Services.GetRequiredService<Decisya.ServiceDefaults.Logging.ILogEnrichmentContext>();
        var tenant = Guid.NewGuid().ToString("D");
        var s1 = await SeedSessionAsync(factory, tenant);
        var s2 = await SeedSessionAsync(factory, tenant);

        // Same peer for both sessions: they must not be merged.
        for (var i = 0; i < 3; i++)
        {
            (await StatusAsync(client, Get("/api/tenancy/me", "203.0.113.1", cookie: s1))).Should().Be(HttpStatusCode.OK);
        }

        using var refused = await client.SendAsync(Get("/api/tenancy/me", "203.0.113.1", cookie: s1), ct);
        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        refused.Headers.Contains("Set-Cookie").Should().BeFalse("a refused request sets no cookie");
        api.Requests.Should().HaveCount(3, "the 4th request was never forwarded");

        (await StatusAsync(client, Get("/api/tenancy/me", "203.0.113.1", cookie: s2))).Should().Be(HttpStatusCode.OK);
        api.Requests.Should().HaveCount(4);

        // Another address, same session: one bucket across addresses.
        (await StatusAsync(client, Get("/api/tenancy/me", "203.0.113.9", cookie: s1))).Should().Be(HttpStatusCode.TooManyRequests);

        var events = provider.Records.Where(r => r.EventId == 1820).ToList();
        events.Should().NotBeEmpty();
        events[0].StateText.Should().Contain("PartitionKind=session").And.Contain("RouteClass=api");
        events[0].TenantId.Should().Be(tenant, "the enrichment carries the principal's tenant");
        events[0].UserIdHash.Should().NotBeNullOrEmpty();
        provider.Records.Where(r => r.Category.StartsWith("Decisya.", StringComparison.Ordinal))
            .Should().NotContain(r => r.Contains(s1) || r.Contains("__Host-decisya-session"));
    }

    [Fact]
    public async Task The_admin_class_has_its_own_session_limit_and_a_refused_admin_request_makes_no_api_call()
    {
        var ct = TestContext.Current.CancellationToken;
        await redisFixture.EnsureStartedAsync(ct);
        await using var api = await ApiDouble.StartAsync(ct);
        using var factory = RateLimitFactory.Create(
            Limits(api: 100, admin: 2), redisConnectionString: redisFixture.ConnectionString, apiAddress: api.Address);
        using var client = factory.CreateBffClient();
        var session = await SeedSessionAsync(factory, Guid.NewGuid().ToString("D"));

        (await StatusAsync(client, Get("/api/admin/tenants", "203.0.113.21", cookie: session))).Should().Be(HttpStatusCode.OK);
        (await StatusAsync(client, Get("/api/admin/tenants", "203.0.113.21", cookie: session))).Should().Be(HttpStatusCode.OK);
        (await StatusAsync(client, Get("/api/admin/tenants", "203.0.113.21", cookie: session))).Should().Be(HttpStatusCode.TooManyRequests);
        api.Requests.Should().HaveCount(2);

        (await StatusAsync(client, Get("/api/tenancy/me", "203.0.113.21", cookie: session))).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_refused_oidc_callback_with_a_session_cookie_does_no_session_fixation_work()
    {
        var ct = TestContext.Current.CancellationToken;
        await redisFixture.EnsureStartedAsync(ct);
        using var factory = RateLimitFactory.Create(Limits(login: 1), redisConnectionString: redisFixture.ConnectionString);
        using var client = factory.CreateBffClient();
        var session = await SeedSessionAsync(factory, Guid.NewGuid().ToString("D"));
        var key = SessionKeyExtractor.Extract(factory.Services, session["__Host-decisya-session=".Length..])!;
        var store = factory.Services.GetRequiredService<RedisTicketStore>();

        (await StatusAsync(client, Get("/bff/login", "203.0.113.31"))).Should().Be(HttpStatusCode.Redirect);
        (await StatusAsync(client, Get("/signin-oidc", "203.0.113.31", cookie: session))).Should().Be(HttpStatusCode.TooManyRequests);

        (await store.RetrieveAsync(key)).Should().NotBeNull(
            "the limiter runs before SessionFixationGuard: the refused callback deleted nothing");
    }
}
