using System.Net;
using System.Security.Claims;
using Decisya.Bff.Session;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NodaTime;

namespace Decisya.Bff.Tests;

/// <summary>
/// G4-18-03 (T-10; Story 7): every logout-token validation rule G2 names, against a test
/// RSA key and a static <c>OpenIdConnectConfiguration</c> (no Keycloak container).
/// </summary>
[Trait("Category", "Integration")]
public class BackchannelLogoutTests
{
    private readonly RedisFixture _redisFixture;

    public BackchannelLogoutTests(RedisFixture redisFixture)
    {
        _redisFixture = redisFixture;
    }

    [Theory]
    [MemberData(nameof(InvalidTokenCases))]
    public async Task Invalid_logout_token_is_rejected_and_deletes_nothing(Func<TestSigningContext, string> buildInvalidToken)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _redisFixture.EnsureStartedAsync(cancellationToken);

        using var context = LogoutTokenBuilder.CreateContext(
            "https://test-issuer.decisya.test/realms/decisya", "decisya-bff");
        var configuration = BuildConfiguration(context);
        using var factory = EagerConfigurationGuard.BuildWithRedisConnectionString(
            _redisFixture.ConnectionString,
            () => new BackchannelLogoutTestFactory(_redisFixture.ConnectionString, configuration));

        var sid = Canaries.Unique("sid");
        var sessionKey = await SeedTicketAsync(factory, sid, cancellationToken);

        var invalidToken = buildInvalidToken(context);
        using var response = await PostLogoutTokenAsync(factory, invalidToken, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        body.Should().BeEmpty("the body must name no reason");

        var ticketStore = factory.Services.GetRequiredService<RedisTicketStore>();
        (await ticketStore.RetrieveAsync(sessionKey)).Should().NotBeNull("an invalid token must delete nothing");
    }

    [Fact]
    public async Task A_valid_logout_token_deletes_every_ticket_under_its_sid_and_replay_deletes_nothing_further()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _redisFixture.EnsureStartedAsync(cancellationToken);

        using var context = LogoutTokenBuilder.CreateContext(
            "https://test-issuer.decisya.test/realms/decisya", "decisya-bff");
        var configuration = BuildConfiguration(context);
        using var factory = EagerConfigurationGuard.BuildWithRedisConnectionString(
            _redisFixture.ConnectionString,
            () => new BackchannelLogoutTestFactory(_redisFixture.ConnectionString, configuration));

        var sid = Canaries.Unique("sid");
        var sessionKey = await SeedTicketAsync(factory, sid, cancellationToken);

        var validToken = LogoutTokenBuilder.Build(context, sid: sid);

        using var firstResponse = await PostLogoutTokenAsync(factory, validToken, cancellationToken);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        firstResponse.Headers.CacheControl.Should().NotBeNull();
        firstResponse.Headers.CacheControl!.NoStore.Should().BeTrue();

        var ticketStore = factory.Services.GetRequiredService<RedisTicketStore>();
        (await ticketStore.RetrieveAsync(sessionKey)).Should().BeNull("the valid token should have deleted the ticket");

        // Replay: per the OIDC Back-Channel Logout spec's idempotency expectation, still 200.
        using var replayResponse = await PostLogoutTokenAsync(factory, validToken, cancellationToken);
        replayResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    public static IEnumerable<object[]> InvalidTokenCases()
    {
        yield return
        [
            new Func<TestSigningContext, string>(context =>
                LogoutTokenBuilder.Build(context, signingKey: LogoutTokenBuilder.CreateUnrelatedSigningKey())),
        ];
        yield return [new Func<TestSigningContext, string>(context => LogoutTokenBuilder.Build(context, algorithm: "none"))];
        yield return
        [
            new Func<TestSigningContext, string>(context => LogoutTokenBuilder.Build(
                context,
                signingKey: LogoutTokenBuilder.CreateHmacKeyFromRsaPublicKey(context),
                algorithm: SecurityAlgorithms.HmacSha256)),
        ];
        yield return [new Func<TestSigningContext, string>(context => LogoutTokenBuilder.Build(context, issuer: "https://attacker.test/realms/decisya"))];
        yield return [new Func<TestSigningContext, string>(context => LogoutTokenBuilder.Build(context, audience: "some-other-client"))];
        yield return [new Func<TestSigningContext, string>(context => LogoutTokenBuilder.Build(context, includeEvents: false))];
        yield return [new Func<TestSigningContext, string>(context => LogoutTokenBuilder.Build(context, includeNonce: true))];
        yield return [new Func<TestSigningContext, string>(context => LogoutTokenBuilder.Build(context, sid: null))];
        yield return
        [
            new Func<TestSigningContext, string>(context => LogoutTokenBuilder.Build(
                context, issuedAtUnixSeconds: NodaTime.SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromMinutes(5)).ToUnixTimeSeconds())),
        ];
        yield return
        [
            new Func<TestSigningContext, string>(context => LogoutTokenBuilder.Build(
                context, expiresAtUnixSeconds: NodaTime.SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromMinutes(5)).ToUnixTimeSeconds())),
        ];
    }

    private static OpenIdConnectConfiguration BuildConfiguration(TestSigningContext context)
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = context.Issuer };
        configuration.SigningKeys.Add(context.SigningKey);
        return configuration;
    }

    private static async Task<string> SeedTicketAsync(
        BackchannelLogoutTestFactory factory, string sid, CancellationToken cancellationToken)
    {
        var identity = new ClaimsIdentity([new Claim("sid", sid), new Claim("sub", Canaries.Unique("sub"))], "TestSeed");
        var principal = new ClaimsPrincipal(identity);
        var properties = new AuthenticationProperties
        {
            ExpiresUtc = NodaTime.SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromHours(1)).ToDateTimeOffset(),
        };
        var ticket = new AuthenticationTicket(principal, properties, "Cookies");

        var ticketStore = factory.Services.GetRequiredService<RedisTicketStore>();
        return await ticketStore.StoreAsync(ticket).WaitAsync(cancellationToken);
    }

    private static async Task<HttpResponseMessage> PostLogoutTokenAsync(
        WebApplicationFactory<Program> factory, string logoutToken, CancellationToken cancellationToken)
    {
        // Not disposed here on purpose: disposing the client can dispose the response
        // content before the caller reads it. Test-only, short-lived process; left for GC.
        var client = new HttpClient(factory.Server.CreateHandler()) { BaseAddress = new Uri("https://localhost:7200") };
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = logoutToken });
        return await client.PostAsync("/bff/backchannel-logout", form, cancellationToken);
    }
}
