using System.Net;
using System.Security.Claims;
using Decisya.Bff.Session;
using Decisya.ServiceDefaults.Logging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NodaTime;

namespace Decisya.Bff.Tests;

/// <summary>
/// Issue #121, G2 D4 and G3 G4-121-04: the BFF's sign-in and sign-out events against the real dev
/// realm and Redis. A dev login still completes with the BFF's <c>acr_values=2</c> (the dev realm
/// has no step-up and answers "1"), emits exactly one <c>auth.signin.succeeded</c> after the ticket is
/// stored, and sign-out and back-channel logout each emit one <c>auth.signout</c> with the right
/// initiator. The hashed <c>user_id</c> arrives only through the enrichment.
/// </summary>
[Trait("Category", "Integration")]
public class BffAuthEventsIntegrationTests
{
    private const string DevAliceTenantId = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    private readonly KeycloakBffFixture _keycloakFixture;
    private readonly RedisFixture _redisFixture;

    public BffAuthEventsIntegrationTests(KeycloakBffFixture keycloakFixture, RedisFixture redisFixture)
    {
        _keycloakFixture = keycloakFixture;
        _redisFixture = redisFixture;
    }

    [Theory]
    [InlineData("dev-alice", DevAliceTenantId)]
    [InlineData("dev-admin", null)]
    public async Task A_dev_login_with_acr_values_2_completes_and_logs_exactly_one_succeeded_event(string username, string? expectedTenant)
    {
        var ct = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(ct);
        await _redisFixture.EnsureStartedAsync(ct);
        var provider = new CapturingLoggerProvider();
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, loggerProvider: provider);
        provider.Enrichment = factory.Services.GetRequiredService<ILogEnrichmentContext>();

        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, username, _keycloakFixture.DevUserPassword, "/dashboard", ct);

        result.BffJar.Cookies.Should().ContainKey("__Host-decisya-session", "the dev login must complete: the ticket is stored and the cookie issued");
        var events = provider.Records.Where(r => r.EventName == "auth.signin.succeeded").ToList();
        events.Should().ContainSingle();
        events[0].Level.Should().Be(LogLevel.Information);
        events[0].StateText.Should().Contain("Acr=1", "the dev realm has no level-of-authentication conditions and answers \"1\" to acr_values=2");
        events[0].TenantId.Should().Be(expectedTenant);
        events[0].UserIdHash.Should().NotBeNullOrEmpty();
        provider.Records.Where(r => r.EventName == "auth.signin.failed").Should().BeEmpty();

        // The raw subject, the tenant claim as a claim, and the tokens never reach a message or a state value.
        using var me = await result.BffClient.GetAsync("/bff/me", ct);
        var sub = System.Text.Json.JsonDocument.Parse(await me.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("sub").GetString()!;
        events[0].Contains(sub).Should().BeFalse();
        events[0].UserIdHash.Should().NotContain(sub);
        events[0].Message.Should().NotContain("@");
    }

    [Fact]
    public async Task Signing_out_logs_one_signout_event_initiated_by_the_user()
    {
        var ct = TestContext.Current.CancellationToken;
        await _keycloakFixture.EnsureStartedAsync(ct);
        await _redisFixture.EnsureStartedAsync(ct);
        var provider = new CapturingLoggerProvider();
        using var factory = BffFactoryFactory.Create(_keycloakFixture, _redisFixture, loggerProvider: provider);
        provider.Enrichment = factory.Services.GetRequiredService<ILogEnrichmentContext>();
        var result = await LoginFlowHarness.LogInAsync(
            factory, _keycloakFixture, "dev-bob", _keycloakFixture.DevUserPassword, "/dashboard", ct);
        using (var me = await result.BffClient.GetAsync("/bff/me", ct))
        {
            me.EnsureSuccessStatusCode();
        }

        result.BffJar.Cookies.TryGetValue("__Host-decisya-xsrf", out var xsrf).Should().BeTrue();
        provider.Records.Clear();

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/bff/logout");
        logout.Headers.Add("X-XSRF-TOKEN", xsrf);
        using var response = await result.BffClient.SendAsync(logout, ct);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var events = provider.Records.Where(r => r.EventName == "auth.signout").ToList();
        events.Should().ContainSingle();
        events[0].Level.Should().Be(LogLevel.Information);
        events[0].StateText.Should().Contain("Initiator=user");
        events[0].UserIdHash.Should().NotBeNullOrEmpty();
        events[0].TenantId.Should().Be("2f1a8d5e-3b4c-4e6f-9a7b-1c2d3e4f5a6b");
    }

    [Fact]
    public async Task A_valid_backchannel_logout_that_removed_a_session_logs_one_signout_event_and_nothing_else_does()
    {
        var ct = TestContext.Current.CancellationToken;
        await _redisFixture.EnsureStartedAsync(ct);
        using var context = LogoutTokenBuilder.CreateContext("https://test-issuer.decisya.test/realms/decisya", "decisya-bff");
        var configuration = new OpenIdConnectConfiguration { Issuer = context.Issuer };
        configuration.SigningKeys.Add(context.SigningKey);
        var provider = new CapturingLoggerProvider();
        using var factory = EagerConfigurationGuard.BuildWithRedisConnectionString(
            _redisFixture.ConnectionString,
            () => new BackchannelLogoutTestFactory(_redisFixture.ConnectionString, configuration, provider));
        provider.Enrichment = factory.Services.GetRequiredService<ILogEnrichmentContext>();
        var sid = Canaries.Unique("sid");
        var sub = Canaries.Unique("sub");
        await SeedTicketAsync(factory, sid);

        // An invalid token, a valid token for an unknown session: no event.
        using (var invalid = await PostAsync(factory, LogoutTokenBuilder.Build(context, sid: sid, issuer: "https://attacker.test/realms/decisya"), ct))
        {
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        using (var unknown = await PostAsync(factory, LogoutTokenBuilder.Build(context, sid: Canaries.Unique("other-sid"), subject: sub), ct))
        {
            unknown.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        provider.Records.Where(r => r.EventName == "auth.signout").Should().BeEmpty();

        // The valid token that removes the session: one event, then a replay removes nothing and logs nothing.
        var valid = LogoutTokenBuilder.Build(context, sid: sid, subject: sub);
        using (var first = await PostAsync(factory, valid, ct))
        {
            first.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (var replay = await PostAsync(factory, valid, ct))
        {
            replay.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var events = provider.Records.Where(r => r.EventName == "auth.signout").ToList();
        events.Should().ContainSingle();
        events[0].StateText.Should().Contain("Initiator=backchannel");
        events[0].UserIdHash.Should().NotBeNullOrEmpty().And.NotContain(sub);
        provider.Records.Where(r => r.Contains(sub)).Should().BeEmpty("the raw sub never reaches a record");
    }

    private static async Task SeedTicketAsync(WebApplicationFactory<Program> factory, string sid)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sid", sid), new Claim("sub", Canaries.Unique("ticket-sub"))], "TestSeed"));
        var properties = new AuthenticationProperties
        {
            ExpiresUtc = NodaTime.SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromHours(1)).ToDateTimeOffset(),
        };

        await factory.Services.GetRequiredService<RedisTicketStore>().StoreAsync(new AuthenticationTicket(principal, properties, "Cookies"));
    }

    private static async Task<HttpResponseMessage> PostAsync(WebApplicationFactory<Program> factory, string logoutToken, CancellationToken ct)
    {
        var client = new HttpClient(factory.Server.CreateHandler()) { BaseAddress = new Uri("https://localhost:7200") };
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = logoutToken });
        return await client.PostAsync("/bff/backchannel-logout", form, ct);
    }
}
