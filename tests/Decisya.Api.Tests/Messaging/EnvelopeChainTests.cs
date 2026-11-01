using System.Reflection;
using Decisya.Api.Messaging;
using Decisya.SharedKernel.Tenancy;
using JasperFx.CodeGeneration.Frames;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Runtime.Handlers;

namespace Decisya.Api.Tests.Messaging;

/// <summary>
/// Issue #231, G3 G4-231-07 / architecture rule R1: the receive middleware in real, generated handler
/// chains. The test host is the Production-environment host built from <c>AddDecisyaMessaging</c>
/// exactly as the Api does, with <c>TypeLoadMode</c> (Dynamic, through the test-only runtime compiler)
/// as its only Wolverine override.
/// </summary>
[Trait("Category", "Unit")]
[Collection(ChainHostDefinition.Name)]
public class EnvelopeChainTests(ChainHostFixture fixture)
{
    private static readonly TenantId TenantA = TenantId.Parse("018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f70");
    private static readonly TenantId TenantB = TenantId.Parse("018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f71");

    private MessagingHost Host => fixture.Host!;

    [Fact]
    public async Task The_test_hosts_only_Wolverine_override_is_TypeLoadMode()
    {
        await using var production = await MessagingHost.StartAsync("Production", probeHandlers: false);

        production.Runtime.Options.CodeGeneration.TypeLoadMode.Should().Be(JasperFx.CodeGeneration.TypeLoadMode.Static);
        Host.Runtime.Options.CodeGeneration.TypeLoadMode.Should().Be(JasperFx.CodeGeneration.TypeLoadMode.Dynamic);

        var expected = Snapshot(production.Runtime);
        var actual = Snapshot(Host.Runtime);

        actual.Should().BeEquivalentTo(expected, "every other option, value for value, is the Production composition's");
        expected.Should().NotBeEmpty();
        expected.Should().ContainKey("Options.Durability.Mode").WhoseValue.Should().Be(nameof(DurabilityMode.MediatorOnly));
    }

    [Fact]
    public void Every_routable_handler_is_a_generated_chain_with_the_envelope_tenant_middleware_first()
    {
        var graph = Host.Services.GetRequiredService<HandlerGraph>();
        var routable = graph.AllMessageTypes().Where(graph.CanHandle).Distinct().ToList();

        // Wolverine's own handlers: hand-written, no chain, so no middleware. They are the exact
        // allow-list (G3 S-03); anything else that is not a generated chain fails the walk. Each takes
        // a Wolverine-internal message type no module can reference (R5).
        var wolverineOwn = routable
            .Where(t => graph.ChainFor(t) is null)
            .Select(t => t.FullName + "=>" + graph.HandlerFor(t)?.GetType().FullName)
            .Order(StringComparer.Ordinal)
            .ToList();
        wolverineOwn.Should().Equal(
            "Wolverine.Envelope=>Wolverine.Runtime.Scheduled.ScheduledSendEnvelopeHandler",
            "Wolverine.Runtime.Agents.IAgentCommand=>Wolverine.Runtime.Agents.AgentCommandHandler",
            "Wolverine.Runtime.RemoteInvocation.Acknowledgement=>Wolverine.Runtime.RemoteInvocation.AcknowledgementHandler",
            "Wolverine.Runtime.RemoteInvocation.FailureAcknowledgement=>Wolverine.Runtime.RemoteInvocation.FailureAcknowledgementHandler");

        var chained = routable.Where(t => graph.ChainFor(t) is not null).ToList();
        chained.Should().BeEquivalentTo(
            [
                typeof(ProbeMessage), typeof(ProbeStaticMessage), typeof(ProbeInner), typeof(ProbeQuery), typeof(ProbeCascadeMessage),
                typeof(ProbeCascaded), typeof(ProbeStreamQuery),
            ],
            "exactly the listed handlers have a chain");
        graph.AllChains().Select(chain => chain.MessageType).Distinct().Should().BeEquivalentTo(chained);

        foreach (var messageType in chained)
        {
            var handler = graph.HandlerFor(messageType);

            handler.Should().BeAssignableTo<MessageHandler>(messageType.Name + " must be a generated chain handler");
            (handler.GetType().Assembly == typeof(ProbeMessage).Assembly).Should().BeFalse(messageType.Name + " must not be a hand-written (for example pre-canned) handler");
            handler.GetType().Namespace.Should().Be("Internal.Generated.WolverineHandlers");

            var chain = graph.ChainFor(messageType);
            chain.Should().NotBeNull(messageType.Name + " has a chain");

            // The first middleware Decisya (or anyone) adds is the envelope tenant middleware. Wolverine's own
            // MessageFrame, the try/finally wrapper and the log tag frame are not middleware of ours. That the
            // generated code really runs it before the handler is shown by behaviour, in
            // EnvelopeChainRefusalTests (see the G4 report on dependency construction order).
            var firstConstructed = chain!.Middleware.OfType<ConstructorFrame>().FirstOrDefault();
            firstConstructed.Should().NotBeNull(messageType.Name + " has a middleware instance");
            firstConstructed!.Variable.VariableType.Should().Be<EnvelopeTenantMiddleware>();
        }
    }

    public static TheoryData<string, string?, string?, string> Refusals => new()
    {
        { "missing", null, "user-a", "missing" },
        { "empty", string.Empty, "user-a", "missing" },
        { "whitespace", " ", "user-a", "malformed" },
        { "all-zero", "00000000-0000-0000-0000-000000000000", "user-a", "malformed" },
        { "not-a-guid", "not-a-guid", "user-a", "malformed" },
        { "padded", " 018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f70 ", "user-a", "malformed" },
        { "upper", "018F2C9E-6B7A-7C3E-8B1A-2F3C4D5E6F70", "user-a", "malformed" },
        { "default-star", "*DEFAULT*", "user-a", "malformed" },
        { "no-caller-absent", "018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f70", null, "no-caller" },
        { "no-caller-blank", "018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f70", " ", "no-caller" },
    };

    // The unstamped context is the way to put a chosen envelope below the stamp: it is the path the
    // receive middleware exists for.
    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_refused_message_runs_no_handler_and_its_early_built_dependency_is_never_used(string name, string? tenantText, string? caller, string reason)
    {
        var label = "chain-refused-" + name;
        await using var scope = Host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IMessageContext>();
        var options = new DeliveryOptions { TenantId = tenantText };
        if (caller is not null)
        {
            options.Headers[EnvelopeFacts.CallerIdHeader] = caller;
        }

        using var metrics = new RefusalMetrics();
        var builtBefore = Host.Recorder.ConstructedTenants.Count;
        var useReadsBefore = Host.Recorder.UseReads;
        var logsBefore = Host.Logs.Records.Count;

        // No exception reaches the caller.
        await context.InvokeAsync(new ProbeMessage(label), options, TestContext.Current.CancellationToken);

        // (a) Wolverine 6.39.1 builds the dependency before the check, so it saw Invalid.
        var built = Host.Recorder.ConstructedTenants.Skip(builtBefore).ToList();
        built.Should().NotBeEmpty("6.39.1 builds scoped dependencies before the middleware runs, on a refused message too");
        built.Should().OnlyContain(entry => entry.AtConstruction.Kind == TenantResolutionKind.Invalid);

        // (c) The handler never runs, nothing was read at use time, the message scope stays Invalid.
        Host.Recorder.Observed.Any(o => o.Label == label).Should().BeFalse("the handler is not invoked");
        Host.Recorder.UseReads.Should().Be(useReadsBefore, "no use-time read of the tenant or caller happens");
        built.Should().OnlyContain(
            entry => entry.Instance.Resolution.Kind == TenantResolutionKind.Invalid,
            "a refused message never populates the message scope's caller context");
        var requestCallers = built.Select(entry => entry.Instance).OfType<Decisya.Api.Authentication.RequestCaller>().ToList();
        requestCallers.Should().HaveCount(built.Count, "the dependency holds the scope's RequestCaller");
        requestCallers.Should().OnlyContain(rc => !rc.IsPlatformAdmin && !rc.HasMfaLevel);

        // (c) One event, without the payload; the counter goes up by one, with the reason as its only tag.
        var events = Host.Logs.Records.Skip(logsBefore).Where(r => r.EventName == "EnvelopeRefused").ToList();
        events.Should().ContainSingle();
        events[0].StateText.Should().Contain("Reason=" + reason);
        events[0].Contains(label).Should().BeFalse("the payload is never logged");
        metrics.Reasons.Should().Equal([reason]);
    }

    [Fact]
    public async Task On_the_valid_path_the_early_built_dependency_sees_Invalid_at_construction_and_tenant_A_and_caller_A_at_use_time_through_the_same_instance()
    {
        await using var scope = Host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IMessageContext>();
        var options = new DeliveryOptions { TenantId = TenantA.ToString() }.WithHeader(EnvelopeFacts.CallerIdHeader, "user-a");
        var builtBefore = Host.Recorder.ConstructedTenants.Count;

        await context.InvokeAsync(new ProbeMessage("chain-valid"), options, TestContext.Current.CancellationToken);

        var observed = Host.Recorder.Observed.Single(o => o.Label == "chain-valid");
        observed.Tenant.Should().Be(TenantResolution.For(TenantA));
        observed.UserId.Should().Be("user-a");
        observed.IsPlatformAdmin.Should().BeFalse();
        observed.EnrichedTenant.Should().Be(TenantA.ToString());
        observed.EnrichedUserHash.Should().NotBeNullOrEmpty().And.NotBe("user-a");

        // (a) built before the check ...
        observed.TenantSeenByDb.Kind.Should().Be(TenantResolutionKind.Invalid);
        // (e) ... and reading tenant A and caller A at use time, through the instance the middleware set.
        observed.TenantAtUse.Should().Be(TenantResolution.For(TenantA));
        observed.CallerAtUse.Should().Be("user-a");
        observed.DbSharesTheHandlersTenantInstance.Should().BeTrue("the dependency and the handler hold the one ICurrentTenant instance the middleware set");

        // The scope's RequestCaller was set once, with both facts false.
        var built = Host.Recorder.ConstructedTenants.Skip(builtBefore).Single().Instance.Should().BeOfType<Decisya.Api.Authentication.RequestCaller>().Subject;
        built.Resolution.Should().Be(TenantResolution.For(TenantA));
        built.IsPlatformAdmin.Should().BeFalse();
        built.HasMfaLevel.Should().BeFalse();
    }

    [Fact]
    public async Task Concurrent_messages_of_two_tenants_through_real_chains_each_see_only_their_own_tenant_caller_and_enrichment()
    {
        const int PerTenant = 8;
        var arrivedBefore = Host.Recorder.Arrived;

        var messages = Enumerable.Range(0, 2 * PerTenant)
            .Select(i => Task.Run(
                async () =>
                {
                    var (tenant, user) = i % 2 == 0 ? (TenantA, "caller-a") : (TenantB, "caller-b");
                    await using var scope = Host.CallerScope(tenant, user);
                    await scope.ServiceProvider.GetRequiredService<IMessageBus>()
                        .InvokeAsync(new ProbeMessage("gate-" + i), TestContext.Current.CancellationToken);
                },
                TestContext.Current.CancellationToken))
            .ToList();

        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (Host.Recorder.Arrived - arrivedBefore < 2 * PerTenant && waited.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        (Host.Recorder.Arrived - arrivedBefore).Should().Be(2 * PerTenant, "every message must be inside its handler at once");
        Host.Recorder.Release.SetResult();
        await Task.WhenAll(messages);

        for (var i = 0; i < 2 * PerTenant; i++)
        {
            var (tenant, user) = i % 2 == 0 ? (TenantA, "caller-a") : (TenantB, "caller-b");
            var seen = Host.Recorder.Observed.Single(o => o.Label == "gate-" + i);
            seen.EnvelopeTenant.Should().Be(tenant.ToString());
            seen.Tenant.Should().Be(TenantResolution.For(tenant));
            seen.UserId.Should().Be(user);
            seen.CallerHeader.Should().Be(user);
            seen.EnrichedTenant.Should().Be(tenant.ToString());

            // (e) each message's early-built dependency reads its own message's tenant and caller at use time.
            seen.TenantSeenByDb.Kind.Should().Be(TenantResolutionKind.Invalid);
            seen.TenantAtUse.Should().Be(TenantResolution.For(tenant));
            seen.CallerAtUse.Should().Be(user);
            seen.DbSharesTheHandlersTenantInstance.Should().BeTrue();
        }
    }

    private static SortedDictionary<string, string> Snapshot(Wolverine.Runtime.IWolverineRuntime runtime)
    {
        var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);

        void Add(string prefix, object instance)
        {
            foreach (var property in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (!property.CanRead || property.GetIndexParameters().Length > 0
                    || !(type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(TimeSpan)))
                {
                    continue;
                }

                // Per-host identity, not configuration.
                if (property.Name is "AssignedNodeNumber" or "ServiceName" || property.Name.Contains("First", StringComparison.Ordinal))
                {
                    continue;
                }

                snapshot[prefix + property.Name] = Convert.ToString(property.GetValue(instance), System.Globalization.CultureInfo.InvariantCulture) ?? "(null)";
            }
        }

        Add("Options.", runtime.Options);
        Add("Options.Durability.", runtime.Options.Durability);
        snapshot["Options.DefaultSerializer"] = runtime.Options.DefaultSerializer.ContentType;
        snapshot["Options.Transports"] = string.Join(",", runtime.Options.Transports.Select(t => t.Protocol).Order(StringComparer.Ordinal));
        snapshot["Options.MetadataRules"] = runtime.Options.MetadataRules.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        snapshot["Options.Policies"] = string.Join(",", runtime.Options.Policies.Select(p => p.GetType().FullName).Order(StringComparer.Ordinal));
        snapshot["Storage"] = runtime.Storage.GetType().FullName!;
        return snapshot;
    }
}
