using System.Net;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using static Decisya.Bff.Tests.RateLimiting.RateLimitRequests;

namespace Decisya.Bff.Tests.RateLimiting;

/// <summary>
/// #122 G5, Story 3 and NFR-52: a scripted single-user journey at the production default limits gets no 429.
/// The Development settings file raises the limits, so the defaults are set back explicitly. The sign-in is
/// the BFF's own login start plus a seeded session (the identity provider is not part of this lane); the
/// Api is the loopback ApiDouble; the sessions are real tickets in Redis.
/// </summary>
[Trait("Category", "Integration")]
public class HouseholdJourneyTests(RedisFixture redisFixture)
{
    private const string Peer = "198.51.100.150";
    private const string Realm = "https://example.test/realms/decisya";

    [Fact]
    public async Task A_household_journey_at_the_default_limits_gets_no_429()
    {
        var ct = TestContext.Current.CancellationToken;
        await redisFixture.EnsureStartedAsync(ct);
        await using var api = await ApiDouble.StartAsync(ct);
        var oidc = new OpenIdConnectConfiguration
        {
            Issuer = Realm,
            AuthorizationEndpoint = Realm + "/protocol/openid-connect/auth",
            TokenEndpoint = Realm + "/protocol/openid-connect/token",
            EndSessionEndpoint = Realm + "/protocol/openid-connect/logout",
        };
        using var factory = RateLimitFactory.Create(
            Limits(login: 10, backchannel: 300, api: 300, anonymous: 60, admin: 30, windowSeconds: 60),
            redisConnectionString: redisFixture.ConnectionString,
            apiAddress: api.Address,
            oidcConfiguration: oidc);
        using var client = factory.CreateBffClient();
        var statuses = new List<HttpStatusCode>();

        // Sign-in: the start of the flow, then the session the callback would have created.
        statuses.Add(await StatusAsync(client, Get("/bff/login?returnUrl=/dashboard", Peer)));
        var session = await SessionSeeding.SeedAsync(factory);

        // Load the shell and open every page (the SPA's client-side routes are served by the shell).
        foreach (var page in new[] { "/", "/dashboard", "/accounts", "/budgets", "/settings" })
        {
            statuses.Add(await StatusAsync(client, Get(page, Peer, cookie: session.Cookie)));
        }

        // /bff/me, which also issues the antiforgery cookie for the sign-out.
        string[] antiforgeryCookies;
        using (var me = await client.SendAsync(Get("/bff/me", Peer, cookie: session.Cookie), ct))
        {
            statuses.Add(me.StatusCode);
            me.StatusCode.Should().Be(HttpStatusCode.OK);
            antiforgeryCookies = me.Headers.TryGetValues("Set-Cookie", out var setCookies)
                ? [.. setCookies.Select(value => value.Split(';')[0])]
                : [];
        }

        // 40 API calls in one minute.
        for (var i = 0; i < 40; i++)
        {
            statuses.Add(await StatusAsync(client, Get("/api/tenancy/me", Peer, cookie: session.Cookie)));
        }

        api.Requests.Should().HaveCount(40, "every API call of the journey reached the Api");

        // Sign-out with the double-submit antiforgery pair.
        var xsrf = antiforgeryCookies.FirstOrDefault(c => c.StartsWith("__Host-decisya-xsrf=", StringComparison.Ordinal));
        using var logout = new HttpRequestMessage(HttpMethod.Post, "/bff/logout");
        logout.Headers.TryAddWithoutValidation(RateLimitFactory.PeerHeader, Peer);
        logout.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", antiforgeryCookies.Prepend(session.Cookie)));
        if (xsrf is not null)
        {
            logout.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", xsrf["__Host-decisya-xsrf=".Length..]);
        }

        logout.Headers.TryAddWithoutValidation("Accept", "application/json");
        using (var response = await client.SendAsync(logout, ct))
        {
            statuses.Add(response.StatusCode);
        }

        statuses.Should().NotContain(HttpStatusCode.TooManyRequests, "NFR-52: 0 responses of 429 in a normal journey");
        statuses.Count(s => s == HttpStatusCode.OK).Should().BeGreaterThanOrEqualTo(42, "me and the 40 API calls succeeded, plus the sign-out");
    }
}
