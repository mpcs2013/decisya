using System.Net;
using Decisya.Bff.RateLimiting;
using Decisya.Bff.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using static Decisya.Bff.Tests.RateLimiting.RateLimitRequests;

namespace Decisya.Bff.Tests.RateLimiting;

/// <summary>
/// #122 G5, Story 2 with real logout tokens and real sessions in Redis: a token refused with 429 is lost and
/// its session stays alive (no retry is assumed or sent), and legitimate logout traffic at the default limit
/// is never refused. The Development file raises the limits, so the defaults are set back explicitly.
/// </summary>
[Trait("Category", "Integration")]
public class BackchannelLogoutLimitTests(RedisFixture redisFixture)
{
    private const string Issuer = "https://test-issuer.decisya.test/realms/decisya";
    private const string Peer = "198.51.100.140";

    private static OpenIdConnectConfiguration OidcConfiguration(TestSigningContext context)
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = context.Issuer };
        configuration.SigningKeys.Add(context.SigningKey);
        return configuration;
    }

    private static Dictionary<string, string?> ProductionDefaults() =>
        Limits(login: 10, backchannel: 300, api: 300, anonymous: 60, admin: 30, windowSeconds: 60);

    [Fact]
    public async Task A_refused_logout_token_leaves_the_session_alive_and_is_not_sent_again()
    {
        var ct = TestContext.Current.CancellationToken;
        await redisFixture.EnsureStartedAsync(ct);
        using var context = LogoutTokenBuilder.CreateContext(Issuer, "decisya-bff");
        using var factory = RateLimitFactory.Create(
            Limits(backchannel: 2), redisConnectionString: redisFixture.ConnectionString, oidcConfiguration: OidcConfiguration(context));
        using var client = factory.CreateBffClient();
        var session = await SessionSeeding.SeedAsync(factory);
        var store = factory.Services.GetRequiredService<RedisTicketStore>();

        (await StatusAsync(client, PostForm("/bff/backchannel-logout", new() { ["logout_token"] = "a.b.c" }, Peer))).Should().Be(HttpStatusCode.BadRequest);
        (await StatusAsync(client, PostForm("/bff/backchannel-logout", new() { ["logout_token"] = "a.b.c" }, Peer))).Should().Be(HttpStatusCode.BadRequest);

        var token = LogoutTokenBuilder.Build(context, sid: session.Sid);
        using var refused = await client.SendAsync(PostForm("/bff/backchannel-logout", new() { ["logout_token"] = token }, Peer), ct);

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        refused.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await store.RetrieveAsync(session.TicketKey)).Should().NotBeNull(
            "the refused token was never validated: session S stays alive until its own expiry, and the test does not re-send the token");
    }

    [Fact]
    public async Task Five_valid_logouts_a_minute_at_the_default_limit_are_never_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await redisFixture.EnsureStartedAsync(ct);
        new BffRateLimitOptions().BackchannelLogout.PermitLimit.Should().Be(300, "the default under test");
        using var context = LogoutTokenBuilder.CreateContext(Issuer, "decisya-bff");
        using var factory = RateLimitFactory.Create(
            ProductionDefaults(), redisConnectionString: redisFixture.ConnectionString, oidcConfiguration: OidcConfiguration(context));
        using var client = factory.CreateBffClient();
        var store = factory.Services.GetRequiredService<RedisTicketStore>();

        for (var i = 0; i < 5; i++)
        {
            var session = await SeedAsync(factory);
            var token = LogoutTokenBuilder.Build(context, sid: session.Sid);

            (await StatusAsync(client, PostForm("/bff/backchannel-logout", new() { ["logout_token"] = token }, Peer)))
                .Should().Be(HttpStatusCode.OK, $"logout number {i + 1} is legitimate traffic");
            (await store.RetrieveAsync(session.TicketKey)).Should().BeNull("a valid token ends its session");
        }
    }

    private static Task<SeededSession> SeedAsync(RateLimitFactory factory) => SessionSeeding.SeedAsync(factory);
}
