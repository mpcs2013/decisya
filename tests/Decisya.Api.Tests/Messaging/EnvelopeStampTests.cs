using Decisya.Api.Messaging;
using Decisya.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace Decisya.Api.Tests.Messaging;

/// <summary>
/// Issue #231, G3 G4-231-01 and G4-231-06 / architecture rule R2: the send stamp is the one
/// authoritative writer of the envelope tenant and caller id. Each case runs a real Wolverine runtime
/// (in-process, <c>MediatorOnly</c>) with real generated handler chains and reads what the handler saw.
/// </summary>
/// <remarks>
/// Wolverine 6.39.1 in MediatorOnly mode applies no <c>IEnvelopeRule</c> to <c>InvokeAsync</c> and
/// rejects publish, send, schedule and cascade outright, so the paths that exist are the invoke and
/// stream calls of the scope's <see cref="IMessageBus"/>, and the rest must throw. The stamp's own
/// ordering (it throws before the inner bus is called) is in <see cref="EnvelopeStampInnerBusTests"/>.
/// </remarks>
[Trait("Category", "Unit")]
[Collection(ChainHostDefinition.Name)]
public class EnvelopeStampTests(ChainHostFixture fixture)
{
    private static readonly TenantId TenantA = TenantId.Parse("018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f70");
    private static readonly TenantId TenantB = TenantId.Parse("018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f71");

    private const string UserA = "user-a";

    private MessagingHost Host => fixture.Host!;

    [Fact]
    public async Task InvokeAsync_arrives_as_the_senders_tenant_with_the_senders_caller_id()
    {
        await using var scope = Host.CallerScope(TenantA, UserA);
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        await bus.InvokeAsync(new ProbeMessage("stamp-invoke"), TestContext.Current.CancellationToken);

        AssertArrivedAs("stamp-invoke", TenantA, UserA);
    }

    [Fact]
    public async Task InvokeAsync_with_a_response_arrives_as_the_senders_tenant()
    {
        await using var scope = Host.CallerScope(TenantA, UserA);
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var answer = await bus.InvokeAsync<ProbeAnswer>(new ProbeQuery("stamp-response"), TestContext.Current.CancellationToken);

        answer.Label.Should().Be("stamp-response");
        AssertArrivedAs("stamp-response", TenantA, UserA);
    }

    [Fact]
    public async Task InvokeAsync_with_options_arrives_as_the_senders_tenant_and_stamps_the_options_in_place()
    {
        await using var scope = Host.CallerScope(TenantA, UserA);
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        var options = new DeliveryOptions();

        await bus.InvokeAsync(new ProbeMessage("stamp-options"), options, TestContext.Current.CancellationToken);

        AssertArrivedAs("stamp-options", TenantA, UserA);
        options.TenantId.Should().Be(TenantA.ToString());
    }

    // G3 T-03: a chosen tenant is accepted by a receiver, so the stamp must not keep one.
    [Fact]
    public async Task A_foreign_tenant_and_a_forged_caller_in_DeliveryOptions_are_overwritten()
    {
        await using var scope = Host.CallerScope(TenantA, UserA);
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        var foreign = new DeliveryOptions { TenantId = TenantB.ToString() }
            .WithHeader(EnvelopeFacts.CallerIdHeader, "forged-caller");

        await bus.InvokeAsync(new ProbeMessage("stamp-foreign"), foreign, TestContext.Current.CancellationToken);
        await bus.InvokeAsync<ProbeAnswer>(new ProbeQuery("stamp-foreign-response"), foreign, TestContext.Current.CancellationToken);

        AssertArrivedAs("stamp-foreign", TenantA, UserA);
        AssertArrivedAs("stamp-foreign-response", TenantA, UserA);
    }

    // G4-231-07 (G3 fallback): Wolverine 6.39.1 carries the tenant onto a nested invoke from a handler but
    // not the decisya-caller-id header, so the receive middleware refuses the nested message as no-caller.
    // This pins that refusal. The gap goes to G2: the fix is never a relaxed caller check or an invented
    // actor (G3 decision on no-caller).
    [Fact]
    public async Task A_message_invoked_from_a_real_handler_is_refused_as_no_caller_because_the_caller_header_is_not_carried()
    {
        await using var scope = Host.CallerScope(TenantA, UserA);
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        var logsBefore = Host.Logs.Records.Count;

        await bus.InvokeAsync(new ProbeMessage("nested-a"), TestContext.Current.CancellationToken);

        AssertArrivedAs("nested-a", TenantA, UserA);
        Host.Recorder.Observed.Any(o => o.Label == "nested-a-inner").Should().BeFalse("the nested handler must not run without a caller id");
        var refusals = Host.Logs.Records.Skip(logsBefore).Where(r => r.EventName == "EnvelopeRefused").ToList();
        refusals.Should().ContainSingle();
        refusals[0].StateText.Should().Contain("Reason=no-caller").And.Contain("MessageType=" + typeof(ProbeInner).FullName);
    }

    [Fact]
    public async Task InvokeForTenantAsync_and_a_set_TenantId_are_not_supported()
    {
        await using var scope = Host.CallerScope(TenantA, UserA);
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var forTenant = () => bus.InvokeForTenantAsync(TenantB.ToString(), new ProbeMessage("stamp-for-tenant"), TestContext.Current.CancellationToken);
        var forTenantResponse = () => bus.InvokeForTenantAsync<ProbeAnswer>(TenantB.ToString(), new ProbeQuery("stamp-for-tenant"), TestContext.Current.CancellationToken);
        var setTenant = () => bus.TenantId = TenantB.ToString();

        await forTenant.Should().ThrowAsync<NotSupportedException>();
        await forTenantResponse.Should().ThrowAsync<NotSupportedException>();
        setTenant.Should().Throw<NotSupportedException>();
        bus.TenantId.Should().Be(TenantA.ToString(), "the bus reports the ambient tenant, never one set by hand");
        Host.Recorder.Observed.Should().NotContain(o => o.Label == "stamp-for-tenant");
    }

    [Fact]
    public async Task Two_scopes_stamp_their_own_tenant_and_caller_in_any_order()
    {
        await using var scopeA = Host.CallerScope(TenantA, "caller-one");
        await using var scopeB = Host.CallerScope(TenantB, "caller-two");
        var busA = scopeA.ServiceProvider.GetRequiredService<IMessageBus>();
        var busB = scopeB.ServiceProvider.GetRequiredService<IMessageBus>();

        await busA.InvokeAsync(new ProbeMessage("stamp-from-a"), TestContext.Current.CancellationToken);
        await busB.InvokeAsync(new ProbeMessage("stamp-from-b"), TestContext.Current.CancellationToken);
        await busA.InvokeAsync(new ProbeMessage("stamp-from-a-again"), TestContext.Current.CancellationToken);

        AssertArrivedAs("stamp-from-a", TenantA, "caller-one");
        AssertArrivedAs("stamp-from-b", TenantB, "caller-two");
        AssertArrivedAs("stamp-from-a-again", TenantA, "caller-one");
    }

    [Fact]
    public async Task StreamAsync_arrives_as_the_senders_tenant()
    {
        await using var scope = Host.CallerScope(TenantA, UserA);
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var answers = new List<ProbeAnswer>();
        await foreach (var answer in bus.StreamAsync<ProbeAnswer>(new ProbeStreamQuery("stamp-stream"), TestContext.Current.CancellationToken))
        {
            answers.Add(answer);
        }

        answers.Should().ContainSingle();
        AssertArrivedAs("stamp-stream", TenantA, UserA);
    }

    [Fact]
    public async Task PreviewSubscriptions_either_carries_the_senders_tenant_or_throws()
    {
        await using var scope = Host.CallerScope(TenantA, UserA);
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var preview = bus.PreviewSubscriptions(new ProbeMessage("stamp-preview"), new DeliveryOptions { TenantId = TenantB.ToString() });

        preview.Should().OnlyContain(envelope => envelope.TenantId == TenantA.ToString());
        Host.Recorder.Observed.Should().NotContain(o => o.Label == "stamp-preview", "a preview runs no handler");
    }

    // G4-231-06: a cascade other than the invoke's own response is refused under MediatorOnly, and
    // the cascaded message's handler never runs.
    [Fact]
    public async Task A_cascaded_message_is_refused_under_MediatorOnly_and_its_handler_never_runs()
    {
        await using var scope = Host.CallerScope(TenantA, UserA);
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var act = async () => await bus.InvokeAsync(new ProbeCascadeMessage("stamp-cascade"), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("MediatorOnly");
        Host.Recorder.Observed.Should().ContainSingle(o => o.Label == "stamp-cascade", "the cascading handler itself ran once");
        Host.Recorder.Observed.Should().NotContain(o => o.Handler == nameof(ProbeCascadedHandler) && o.Label.StartsWith("stamp-cascade", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_send_throws_the_stamps_exception_on_every_overload_when_the_ambient_caller_was_never_set()
    {
        await using var scope = Host.Services.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        await AssertEveryCallThrowsAsync(bus, "stamp-invalid-ambient");
    }

    [Fact]
    public async Task A_send_throws_the_stamps_exception_on_every_overload_when_the_ambient_has_no_tenant()
    {
        await using var scope = Host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<Decisya.Api.Authentication.RequestCaller>()
            .Set(TenantResolution.NoTenant, "platform-admin-user", isPlatformAdmin: true);
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        await AssertEveryCallThrowsAsync(bus, "stamp-none-ambient");
    }

    // MediatorOnly: the paths that would reach an envelope store or a transport do not exist.
    [Fact]
    public async Task Publish_send_and_scheduling_are_refused_by_the_in_process_only_mode()
    {
        await using var scope = Host.CallerScope(TenantA, UserA);
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var publish = async () => await bus.PublishAsync(new ProbeMessage("stamp-publish"));
        var send = async () => await bus.SendAsync(new ProbeMessage("stamp-send"));
        var delayed = async () => await bus.SendAsync(new ProbeMessage("stamp-delayed"), new DeliveryOptions { ScheduleDelay = TimeSpan.FromMilliseconds(10) });
        var scheduled = async () => await bus.PublishAsync(
            new ProbeMessage("stamp-scheduled"),
            new DeliveryOptions { ScheduledTime = new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero) });

        (await publish.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("MediatorOnly");
        (await send.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("MediatorOnly");
        (await delayed.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("MediatorOnly");
        (await scheduled.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("MediatorOnly");
        Host.Recorder.Observed.Should().NotContain(o => o.Label.StartsWith("stamp-publish", StringComparison.Ordinal)
            || o.Label.StartsWith("stamp-send", StringComparison.Ordinal)
            || o.Label.StartsWith("stamp-delayed", StringComparison.Ordinal)
            || o.Label.StartsWith("stamp-scheduled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_endpoint_or_topic_is_reachable_from_the_scoped_bus()
    {
        await using var scope = Host.CallerScope(TenantA, UserA);
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var byName = () => bus.EndpointFor("anything");
        var byUri = () => bus.EndpointFor(new Uri("local://anything"));
        var topic = async () => await bus.BroadcastToTopicAsync("anything", new ProbeMessage("stamp-topic"));

        byName.Should().Throw<NotSupportedException>();
        byUri.Should().Throw<NotSupportedException>();
        await topic.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task The_scoped_bus_is_the_stamping_wrapper_and_cannot_be_resolved_from_the_root_provider()
    {
        await using var scope = Host.CallerScope(TenantA, UserA);

        scope.ServiceProvider.GetRequiredService<IMessageBus>().Should().BeOfType<EnvelopeTenantStamp>();
        var rootResolution = () => Host.Services.GetRequiredService<IMessageBus>();
        rootResolution.Should().Throw<InvalidOperationException>();
    }

    private async Task AssertEveryCallThrowsAsync(IMessageBus bus, string label)
    {
        foreach (var (name, call) in BusCalls.All(label, TestContext.Current.CancellationToken))
        {
            var act = () => call(bus);

            var thrown = await act.Should().ThrowAsync<InvalidOperationException>(name + " must throw");
            thrown.Which.Message.Should().Contain("ambient tenant and caller", name + " must throw the stamp's own exception");
        }

        Host.Recorder.Observed.Should().NotContain(o => o.Label == label, "no handler ran");
    }

    private void AssertArrivedAs(string label, TenantId tenant, string callerId)
    {
        var observed = Host.Recorder.Observed.Where(o => o.Label == label).ToList();

        observed.Should().ContainSingle("the message must reach its handler exactly once");
        observed[0].EnvelopeTenant.Should().Be(tenant.ToString());
        observed[0].CallerHeader.Should().Be(callerId);
        observed[0].Tenant.Should().Be(TenantResolution.For(tenant), "the handler's ambient tenant is the envelope tenant");
        observed[0].UserId.Should().Be(callerId);
    }
}
