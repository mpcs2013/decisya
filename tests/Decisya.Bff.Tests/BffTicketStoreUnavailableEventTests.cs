using System.Security.Claims;
using Decisya.Bff.Session;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using NodaTime;
using StackExchange.Redis;

namespace Decisya.Bff.Tests;

/// <summary>
/// Issue #121, Story 6 (Scenario Outline row "the ticket store is unreachable" ->
/// <c>ticket_store_unavailable</c>), G2 D4, G3 G4-121-04: when the ticket cannot be stored, the sign-in
/// fails closed (the exception still propagates, so the caller ends in the generic 500 and no cookie is
/// issued) and exactly one <c>auth.signin.failed</c> Warning with the closed reason is logged. No Docker:
/// the multiplexer points at a closed port and fails fast.
/// </summary>
[Trait("Category", "Unit")]
public class BffTicketStoreUnavailableEventTests
{
    [Fact]
    public async Task An_unreachable_ticket_store_logs_exactly_one_ticket_store_unavailable_event_and_still_fails_closed()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = ConfigurationOptions.Parse("127.0.0.1:1,abortConnect=false,connectTimeout=200,asyncTimeout=1000,syncTimeout=1000");
        options.BacklogPolicy = BacklogPolicy.FailFast;
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(options);

        var provider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(provider));
        var store = new RedisTicketStore(
            multiplexer,
            new EphemeralDataProtectionProvider(),
            NodaTime.SystemClock.Instance,
            loggerFactory.CreateLogger<RedisTicketStore>());

        var sidCanary = Canaries.Unique("sid");
        var subCanary = Canaries.Unique("sub");
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sid", sidCanary), new Claim("sub", subCanary)], "TestSeed"));
        var properties = new AuthenticationProperties
        {
            ExpiresUtc = NodaTime.SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromHours(1)).ToDateTimeOffset(),
        };

        var act = async () => await store.StoreAsync(new AuthenticationTicket(principal, properties, "Cookies"));

        await act.Should().ThrowAsync<Exception>("a ticket that cannot be stored fails the sign-in closed");

        var events = provider.Records.Where(r => r.EventName == "auth.signin.failed").ToList();
        events.Should().ContainSingle();
        events[0].Level.Should().Be(LogLevel.Warning);
        events[0].StateText.Should().Contain("Reason=ticket_store_unavailable");
        events[0].ExceptionText.Should().BeNull("the event is fixed text with no exception parameter");
        provider.Records.Should().NotContain(r => r.Contains(sidCanary) || r.Contains(subCanary), "no claim value reaches a log record");
    }
}
