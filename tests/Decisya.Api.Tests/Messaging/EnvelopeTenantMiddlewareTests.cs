using Decisya.Api.Authentication;
using Decisya.Api.Messaging;
using Decisya.ServiceDefaults.Logging;
using Decisya.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Decisya.Api.Tests.Messaging;

/// <summary>A started messaging host shared by the middleware tests of one class.</summary>
public sealed class MessagingHostFixture : IAsyncLifetime
{
    internal MessagingHost? Host { get; private set; }

    public async ValueTask InitializeAsync() => Host = await MessagingHost.StartAsync(probeHandlers: false);

    public async ValueTask DisposeAsync()
    {
        if (Host is not null)
        {
            await Host.DisposeAsync();
        }
    }
}

/// <summary>
/// Issue #231, G1 Story 1 / G3 G4-231-02 / architecture rule R1: the receive-side envelope tenant
/// middleware. It is driven directly, against the scope's real <see cref="RequestCaller"/> and
/// log-enrichment context, because WolverineFx 6.39.1 ships no runtime compiler and so a generated
/// handler chain cannot run in this repository's tests (reported at G4). What a chain adds (the
/// middleware's place first in every chain; no handler dependency resolving on a refusal) is the one
/// part this class cannot show.
/// </summary>
[Trait("Category", "Unit")]
[Collection(ChainHostDefinition.Name)]
public class EnvelopeTenantMiddlewareTests(MessagingHostFixture fixture) : IClassFixture<MessagingHostFixture>
{
    private const string TenantAText = "018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f70";
    private const string TenantBText = "018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f71";
    private const string Caller = "user-a";
    private const string PayloadCanary = "payload-canary-7f3a";
    private const string EventName = "EnvelopeRefused";

    private MessagingHost Host => fixture.Host!;

    public static TheoryData<string?, string> RefusedTenants => new()
    {
        { null, "missing" },
        { string.Empty, "missing" },
        { " ", "malformed" },
        { "\t", "malformed" },
        { "00000000-0000-0000-0000-000000000000", "malformed" },
        { "not-a-guid-raw-canary-91", "malformed" },
        { " " + TenantAText, "malformed" },
        { TenantAText + " ", "malformed" },
        { TenantAText + "\n", "malformed" },
        { "{" + TenantAText + "}", "malformed" },
        { TenantAText.Replace("-", string.Empty, StringComparison.Ordinal), "malformed" },
        { "0x" + TenantAText[2..], "malformed" },
#pragma warning disable CA1308 // The canonical tenant text is lower case; the upper-case spelling is the case under test.
        { TenantAText.ToUpperInvariant(), "malformed" },
#pragma warning restore CA1308
        { "*DEFAULT*", "malformed" },
    };

    [Theory]
    [MemberData(nameof(RefusedTenants))]
    public void An_envelope_without_a_valid_canonical_tenant_is_refused_before_anything_resolves(string? tenantText, string expectedReason)
    {
        using var scope = Host.Services.CreateScope();
        var (middleware, requestCaller, enrichment) = Build(scope);
        using var metrics = new RefusalMetrics();
        var logsBefore = Host.Logs.Records.Count;

        var continuation = middleware.Before(Envelope(tenantText, Caller));

        continuation.Should().Be(HandlerContinuation.Stop);
        AssertNothingWasSet(requestCaller, enrichment);
        metrics.Reasons.Should().Equal([expectedReason]);
        var events = RefusalEvents(logsBefore);
        events.Should().ContainSingle();
        events[0].StateText.Should().Contain("Reason=" + expectedReason);
        AssertNoSensitiveText(events[0], tenantText);
    }

    [Fact]
    public void A_valid_tenant_and_caller_populate_the_scope_with_the_admin_and_mfa_facts_false()
    {
        using var scope = Host.Services.CreateScope();
        var (middleware, requestCaller, enrichment) = Build(scope);
        using var metrics = new RefusalMetrics();
        var logsBefore = Host.Logs.Records.Count;

        var continuation = middleware.Before(Envelope(TenantAText, Caller));

        continuation.Should().Be(HandlerContinuation.Continue);
        requestCaller.Resolution.Should().Be(TenantResolution.For(TenantId.Parse(TenantAText)));
        requestCaller.UserId.Should().Be(Caller);
        requestCaller.IsPlatformAdmin.Should().BeFalse();
        requestCaller.HasMfaLevel.Should().BeFalse();
        enrichment.TenantId.Should().Be(TenantAText);
        enrichment.UserIdHash.Should().NotBeNullOrEmpty().And.NotBe(Caller, "the user id is stored hashed");
        metrics.Reasons.Should().BeEmpty();
        RefusalEvents(logsBefore).Should().BeEmpty();

        middleware.Finally();
        enrichment.TenantId.Should().BeNull("the log enrichment lasts for the handler's duration only");
    }

    public static TheoryData<string?> RefusedCallers => new()
    {
        { (string?)null },
        { string.Empty },
        { " " },
        { " user-a" },
        { "user-a " },
        { "user\na" },
        { "user\0a" },
        { new string('a', EnvelopeFacts.MaxCallerIdLength + 1) },
    };

    [Theory]
    [MemberData(nameof(RefusedCallers))]
    public void A_valid_tenant_with_no_acceptable_caller_id_is_refused_as_no_caller(string? caller)
    {
        using var scope = Host.Services.CreateScope();
        var (middleware, requestCaller, enrichment) = Build(scope);
        using var metrics = new RefusalMetrics();
        var logsBefore = Host.Logs.Records.Count;

        var continuation = middleware.Before(Envelope(TenantAText, caller));

        continuation.Should().Be(HandlerContinuation.Stop);
        AssertNothingWasSet(requestCaller, enrichment);
        metrics.Reasons.Should().Equal(["no-caller"]);
        var events = RefusalEvents(logsBefore);
        events.Should().ContainSingle();
        events[0].StateText.Should().Contain("Reason=no-caller");
        AssertNoSensitiveText(events[0], TenantAText);
        if (!string.IsNullOrWhiteSpace(caller))
        {
            events[0].Contains(caller).Should().BeFalse("the caller id is never written to the refusal event");
        }
    }

    [Fact]
    public void An_envelope_with_no_caller_header_at_all_is_refused_as_no_caller()
    {
        using var scope = Host.Services.CreateScope();
        var (middleware, requestCaller, enrichment) = Build(scope);
        var envelope = new Envelope(new ProbeMessage(PayloadCanary)) { TenantId = TenantAText };

        middleware.Before(envelope).Should().Be(HandlerContinuation.Stop);

        AssertNothingWasSet(requestCaller, enrichment);
    }

    [Fact]
    public void A_caller_id_of_exactly_128_characters_is_accepted()
    {
        using var scope = Host.Services.CreateScope();
        var (middleware, requestCaller, _) = Build(scope);
        var caller = new string('a', EnvelopeFacts.MaxCallerIdLength);

        middleware.Before(Envelope(TenantAText, caller)).Should().Be(HandlerContinuation.Continue);

        requestCaller.UserId.Should().Be(caller);
        middleware.Finally();
    }

    // A platform-admin or MFA claim travelling in an envelope header is data, never a fact.
    [Fact]
    public void Headers_that_claim_a_role_or_mfa_are_ignored()
    {
        using var scope = Host.Services.CreateScope();
        var (middleware, requestCaller, _) = Build(scope);
        var envelope = Envelope(TenantAText, Caller);
        envelope.Headers["isPlatformAdmin"] = "true";
        envelope.Headers["hasMfaLevel"] = "true";
        envelope.Headers["roles"] = "platform-admin";

        middleware.Before(envelope).Should().Be(HandlerContinuation.Continue);

        requestCaller.IsPlatformAdmin.Should().BeFalse();
        requestCaller.HasMfaLevel.Should().BeFalse();
        middleware.Finally();
    }

    [Fact]
    public void A_scope_already_set_for_the_same_tenant_and_caller_is_accepted_without_a_second_Set()
    {
        using var scope = Host.Services.CreateScope();
        var (middleware, requestCaller, _) = Build(scope);
        requestCaller.Set(TenantResolution.For(TenantId.Parse(TenantAText)), Caller, isPlatformAdmin: false);

        middleware.Before(Envelope(TenantAText, Caller)).Should().Be(HandlerContinuation.Continue);

        middleware.Finally();
    }

    [Fact]
    public void A_scope_already_set_for_another_tenant_or_caller_or_as_an_admin_is_never_re_resolved_from_an_envelope()
    {
        // The last row is G6-231-01: same tenant and caller, but the scope carries an MFA fact.
        // RequestCaller.Set masks an admin fact for a tenant resolution, so a same-tenant admin preset
        // cannot exist (an admin scope is NoTenant, the third row); the check on IsPlatformAdmin is
        // defensive.
        foreach (var preset in new (TenantResolution Resolution, string User, bool Admin, bool Mfa)[]
        {
            (TenantResolution.For(TenantId.Parse(TenantBText)), Caller, false, false),
            (TenantResolution.For(TenantId.Parse(TenantAText)), "someone-else", false, false),
            (TenantResolution.NoTenant, "admin", true, false),
            (TenantResolution.For(TenantId.Parse(TenantAText)), Caller, false, true),
        })
        {
            using var scope = Host.Services.CreateScope();
            var (middleware, requestCaller, enrichment) = Build(scope);
            requestCaller.Set(preset.Resolution, preset.User, preset.Admin, preset.Mfa);
            using var metrics = new RefusalMetrics();

            middleware.Before(Envelope(TenantAText, Caller)).Should().Be(HandlerContinuation.Stop);

            requestCaller.Resolution.Should().Be(preset.Resolution);
            requestCaller.UserId.Should().Be(preset.User);
            enrichment.TenantId.Should().BeNull();
            metrics.Reasons.Should().Equal(["malformed"]);
        }
    }

    [Fact]
    public void A_scope_without_the_request_caller_is_a_wiring_error_not_a_silent_pass()
    {
        var middleware = new EnvelopeTenantMiddleware(
            new TenantOnly(),
            Host.Services.GetRequiredService<ILogEnrichmentContext>(),
            Host.Services.GetRequiredService<ILogger<EnvelopeTenantMiddleware>>());

        var act = () => middleware.Before(Envelope(TenantAText, Caller));

        act.Should().Throw<InvalidOperationException>();
    }

    // T-08: each message gets its own RequestCaller and its own log-enrichment value, so messages
    // handled at the same time never see each other's tenant.
    [Fact]
    public async Task Concurrent_messages_of_two_tenants_each_see_only_their_own_tenant_and_enrichment()
    {
        const int PerTenant = 12;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new CountdownEvent(2 * PerTenant);

        async Task<(string Expected, string? Tenant, string? Enriched, string? User)> OneMessage(string tenantText, string caller)
        {
            using var scope = Host.Services.CreateScope();
            var (middleware, requestCaller, enrichment) = Build(scope);

            middleware.Before(Envelope(tenantText, caller)).Should().Be(HandlerContinuation.Continue);
            started.Signal();
            await release.Task.ConfigureAwait(false);

            var seen = (tenantText, requestCaller.Resolution.TenantId.ToString(), enrichment.TenantId, requestCaller.UserId);
            middleware.Finally();
            return seen;
        }

        var messages = Enumerable.Range(0, 2 * PerTenant)
            .Select(i => i % 2 == 0
                ? Task.Run(() => OneMessage(TenantAText, "caller-a"), TestContext.Current.CancellationToken)
                : Task.Run(() => OneMessage(TenantBText, "caller-b"), TestContext.Current.CancellationToken))
            .ToList();

        started.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).Should().BeTrue("every message must be in flight at once");
        release.SetResult();
        var results = await Task.WhenAll(messages);

        foreach (var result in results)
        {
            result.Tenant.Should().Be(result.Expected);
            result.Enriched.Should().Be(result.Expected);
            result.User.Should().Be(result.Expected == TenantAText ? "caller-a" : "caller-b");
        }

        Host.Services.GetRequiredService<ILogEnrichmentContext>().TenantId.Should().BeNull("no message leaked its enrichment to this flow");
    }

    private static (EnvelopeTenantMiddleware Middleware, RequestCaller Caller, ILogEnrichmentContext Enrichment) Build(IServiceScope scope)
    {
        var requestCaller = scope.ServiceProvider.GetRequiredService<RequestCaller>();
        var enrichment = scope.ServiceProvider.GetRequiredService<ILogEnrichmentContext>();
        var middleware = new EnvelopeTenantMiddleware(
            requestCaller,
            enrichment,
            scope.ServiceProvider.GetRequiredService<ILogger<EnvelopeTenantMiddleware>>());
        return (middleware, requestCaller, enrichment);
    }

    private static Envelope Envelope(string? tenantText, string? callerId)
    {
        var envelope = new Envelope(new ProbeMessage(PayloadCanary)) { TenantId = tenantText };
        if (callerId is not null)
        {
            envelope.Headers[EnvelopeFacts.CallerIdHeader] = callerId;
        }

        return envelope;
    }

    private List<CapturedLogRecord> RefusalEvents(int logsBefore) =>
        [.. Host.Logs.Records.Skip(logsBefore).Where(r => r.EventName == EventName)];

    private static void AssertNothingWasSet(RequestCaller requestCaller, ILogEnrichmentContext enrichment)
    {
        requestCaller.Resolution.Kind.Should().Be(TenantResolutionKind.Invalid, "a refused message never populates the caller context");
        requestCaller.IsPlatformAdmin.Should().BeFalse();
        requestCaller.HasMfaLevel.Should().BeFalse();
        var userId = () => requestCaller.UserId;
        userId.Should().Throw<InvalidOperationException>();
        enrichment.TenantId.Should().BeNull();
    }

    // G1 Story 1: no payload, no raw tenant value, no message body, no caller id in the event.
    private static void AssertNoSensitiveText(CapturedLogRecord record, string? rawTenant)
    {
        record.Level.Should().Be(LogLevel.Warning);
        record.EventId.Should().Be(6000);
        record.Contains(PayloadCanary).Should().BeFalse("the payload is never logged");
        record.Contains(Caller).Should().BeFalse("the caller id is never logged");
        record.ExceptionText.Should().BeNull();
        if (!string.IsNullOrWhiteSpace(rawTenant))
        {
            record.Contains(rawTenant).Should().BeFalse("the raw tenant value is never logged");
        }

        record.StateText.Should().Contain("MessageType=" + typeof(ProbeMessage).FullName);
        record.StateText.Should().Contain("MessageId=");
        record.StateText.Should().Contain("TraceId=");
    }

    private sealed class TenantOnly : ICurrentTenant
    {
        public TenantResolution Resolution => default;
    }
}
