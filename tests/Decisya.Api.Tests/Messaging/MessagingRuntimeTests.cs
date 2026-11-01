using System.Collections;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Decisya.Api.Messaging;
using Decisya.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;

namespace Decisya.Api.Tests.Messaging;

/// <summary>A started Production-environment messaging host shared by the runtime tests of one class.</summary>
public sealed class ProductionMessagingHostFixture : IAsyncLifetime
{
    internal MessagingHost? Host { get; private set; }

    public async ValueTask InitializeAsync() => Host = await MessagingHost.StartAsync("Production", probeHandlers: false);

    public async ValueTask DisposeAsync()
    {
        if (Host is not null)
        {
            await Host.DisposeAsync();
        }
    }
}

public sealed record TenantedMessage(TenantId Tenant, string Note);

public sealed class RequiredTenantMessage
{
    public required TenantId Tenant { get; init; }

    public string? Note { get; init; }
}

/// <summary>
/// Issue #231, G1 Story 2 and G3 G4-231-04 / architecture rule R3: what the composed Production
/// runtime really is, read from <c>IWolverineRuntime</c> after every extension has run, not from the
/// options the code wrote: in-process only, the null store, System.Text.Json the one serializer, and
/// a serializer that cannot build <c>default(TenantId)</c>.
/// </summary>
[Trait("Category", "Unit")]
public class MessagingRuntimeTests(ProductionMessagingHostFixture fixture) : IClassFixture<ProductionMessagingHostFixture>
{
    private static readonly TenantId TenantA = TenantId.Parse("018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f70");

    private MessagingHost Host => fixture.Host!;

    // ADR-0024 point 2: durable messaging gets its own issue (backlog #243 item 231-1). Until then this
    // test is what refuses durable mode, a message store, a transport, a listener or a durable queue.
    [Fact]
    public void Durable_mode_is_off_on_the_composed_Production_runtime()
    {
        var runtime = Host.Runtime;
        var options = runtime.Options;

        options.Durability.Mode.Should().Be(DurabilityMode.MediatorOnly);
        runtime.Storage.Should().BeOfType<NullMessageStore>("no envelope is ever stored in #231");
        runtime.Stores.Main.Should().BeOfType<NullMessageStore>();
        runtime.Stores.HasAnyAncillaryStores().Should().BeFalse();
        options.AutoBuildMessageStorageOnStartup.Should().Be(JasperFx.AutoCreate.None);
        options.DisableAllExternalListeners.Should().BeTrue();
        options.EnableRelayOfUserName.Should().BeFalse();
        options.Durability.EnableMessageDeduplication.Should().BeFalse();
        options.Durability.EnableRecurringMessages.Should().BeFalse();
    }

    // Wolverine always registers the local, stub (in-memory test) and tcp transports. Only the local
    // and stub ones may hold an endpoint or a listener, and none may be durable: a network endpoint
    // or a listener would mean an extension or a later edit switched a transport on (G3 T-12).
    [Fact]
    public void No_network_listener_no_network_endpoint_and_no_durable_queue_exists_on_the_composed_runtime()
    {
        var runtime = Host.Runtime;
        string[] inProcessSchemes = ["local", "stub"];

        runtime.Options.Transports.Select(transport => transport.Protocol)
            .Should().BeEquivalentTo(["local", "stub", "tcp"], "the transports Wolverine always registers");
        runtime.Options.Transports.AllEndpoints()
            .Should().OnlyContain(endpoint => inProcessSchemes.Contains(endpoint.Uri.Scheme));
        runtime.Endpoints.ActiveListeners()
            .Should().OnlyContain(listener => inProcessSchemes.Contains(listener.Uri.Scheme));

        // Wolverine always creates two reserved local queues, "durable" and "scheduled", in Durable
        // mode. With the null store and MediatorOnly they persist nothing; any other durable endpoint
        // would be new.
        runtime.Options.Transports.AllEndpoints()
            .Where(endpoint => endpoint.Mode == EndpointMode.Durable)
            .Select(endpoint => endpoint.EndpointName)
            .Should().BeSubsetOf(["durable", "scheduled"]);
    }

    [Fact]
    public void System_Text_Json_is_the_default_serializer_and_the_only_one_beyond_Wolverines_intrinsic_two()
    {
        var options = Host.Runtime.Options;

        options.DefaultSerializer.ContentType.Should().Be("application/json");
        options.DefaultSerializer.GetType().Name.Should().Be("SystemTextJsonSerializer");

        // Wolverine's own envelope and wolverine-binary formats are always registered and are not
        // used for in-process handling. Any other entry is a serializer an extension added.
        var field = typeof(WolverineOptions).GetField("_serializers", BindingFlags.NonPublic | BindingFlags.Instance);
        field.Should().NotBeNull("WolverineOptions no longer holds a _serializers registry; re-verify the serializer allow-list");
        var registry = (IDictionary)field!.GetValue(options)!;
        registry.Keys.Cast<string>().Order(StringComparer.Ordinal)
            .Should().Equal("application/json", "binary/envelope", "binary/wolverine");
    }

    [Fact]
    public void A_message_with_a_tenant_round_trips_through_the_configured_serializer()
    {
        var serializer = Host.Runtime.Options.DefaultSerializer;
        var message = new TenantedMessage(TenantA, "hello");

        var read = (TenantedMessage)serializer.ReadFromData(typeof(TenantedMessage), EnvelopeWith(serializer.WriteMessage(message)))!;

        read.Should().Be(message);
    }

    [Fact]
    public void A_payload_whose_tenant_is_missing_fails_to_deserialize_for_a_constructor_parameter_and_for_a_required_member()
    {
        var serializer = Host.Runtime.Options.DefaultSerializer;
        var constructorPayload = WithoutProperty(serializer.WriteMessage(new TenantedMessage(TenantA, "hello")), "Tenant");
        var requiredPayload = WithoutProperty(
            serializer.WriteMessage(new RequiredTenantMessage { Tenant = TenantA, Note = "hello" }), "Tenant");

        var constructorRead = () => serializer.ReadFromData(typeof(TenantedMessage), EnvelopeWith(constructorPayload));
        var requiredRead = () => serializer.ReadFromData(typeof(RequiredTenantMessage), EnvelopeWith(requiredPayload));

        constructorRead.Should().Throw<JsonException>("no message object holding default(TenantId) may reach a handler");
        requiredRead.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public void A_payload_whose_tenant_is_the_empty_guid_or_malformed_fails_to_deserialize(string badTenant)
    {
        var serializer = Host.Runtime.Options.DefaultSerializer;
        var payload = WithProperty(serializer.WriteMessage(new TenantedMessage(TenantA, "hello")), "Tenant", JsonValue.Create(badTenant));

        var read = () => serializer.ReadFromData(typeof(TenantedMessage), EnvelopeWith(payload));

        read.Should().Throw<JsonException>();
    }

    [Fact]
    public void A_payload_whose_tenant_is_json_null_fails_to_deserialize()
    {
        var serializer = Host.Runtime.Options.DefaultSerializer;
        var payload = WithProperty(serializer.WriteMessage(new TenantedMessage(TenantA, "hello")), "Tenant", null);

        var read = () => serializer.ReadFromData(typeof(TenantedMessage), EnvelopeWith(payload));

        read.Should().Throw<JsonException>();
    }

    [Fact]
    public void A_null_for_a_non_nullable_member_fails_to_deserialize_because_nullable_annotations_are_respected()
    {
        var serializer = Host.Runtime.Options.DefaultSerializer;
        var payload = WithProperty(serializer.WriteMessage(new TenantedMessage(TenantA, "hello")), "Note", null);

        var read = () => serializer.ReadFromData(typeof(TenantedMessage), EnvelopeWith(payload));

        read.Should().Throw<JsonException>();
    }

    [Fact]
    public void A_message_with_no_tenant_fails_at_the_sender_not_at_the_receiver()
    {
        var serializer = Host.Runtime.Options.DefaultSerializer;

        var write = () => serializer.WriteMessage(new TenantedMessage(default, "hello"));

        write.Should().Throw<JsonException>();
    }

    // G3 S-05 / T-14: Wolverine's own meter and activity source are not subscribed, because their tags
    // carry tenant ids; the controls prove the subscription for the Decisya sources is live.
    [Fact]
    public async Task Wolverines_own_meter_and_activity_source_are_not_subscribed_but_Decisya_ones_are()
    {
        // The metric reader exists only when an OTLP endpoint is configured (nothing is sent in this
        // test: the endpoint is a closed local port and no interval elapses).
        await using var exporting = await MessagingHost.StartAsync(
            "Production",
            new Dictionary<string, string?> { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:1" },
            probeHandlers: false);

        // The runtime names its meter "Wolverine:<service name>", so the plain and the suffixed names
        // are both checked, next to the runtime's own meter.
        exporting.Runtime.Meter.Name.Should().StartWith("Wolverine");
        using var wolverineSource = new ActivitySource("Wolverine");
        using var wolverineServiceSource = new ActivitySource(exporting.Runtime.Meter.Name);
        using var decisyaSource = new ActivitySource(MessagingTelemetry.Name);
        using var wolverineMeter = new Meter("Wolverine");
        var wolverineCounter = wolverineMeter.CreateCounter<long>("probe.count");
        var runtimeCounter = exporting.Runtime.Meter.CreateCounter<long>("probe.runtime.count");
        var decisyaCounter = MessagingTelemetry.Meter.CreateCounter<long>("probe.count");

        wolverineSource.HasListeners().Should().BeFalse();
        wolverineServiceSource.HasListeners().Should().BeFalse();
        wolverineCounter.Enabled.Should().BeFalse();
        runtimeCounter.Enabled.Should().BeFalse();
        decisyaSource.HasListeners().Should().BeTrue("the OpenTelemetry subscription for Decisya.* must be live for this test to prove anything");
        decisyaCounter.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task The_Warning_floor_holds_for_the_Wolverine_and_JasperFx_categories_whatever_the_configuration_says()
    {
        await using var lowered = await MessagingHost.StartAsync(
            "Production",
            new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Trace",
                ["Logging:LogLevel:Wolverine"] = "Debug",
                ["Logging:LogLevel:Wolverine.Runtime"] = "Trace",
                ["Logging:LogLevel:JasperFx"] = "Debug",
                ["Logging:Console:LogLevel:Default"] = "Trace",
                ["Logging:Console:LogLevel:Wolverine"] = "Trace",
                ["Logging:LogLevel:Decisya.Control"] = "Debug",
            },
            probeHandlers: false);
        var factory = lowered.Services.GetRequiredService<ILoggerFactory>();

        foreach (var category in new[]
        {
            "Wolverine", "Wolverine.Runtime.WolverineRuntime", "Wolverine.Configuration.HandlerDiscovery", "JasperFx", "JasperFx.CodeGeneration.GenerationRules",
        })
        {
            var logger = factory.CreateLogger(category);
            logger.IsEnabled(LogLevel.Information).Should().BeFalse(category + " must not log Information");
            logger.IsEnabled(LogLevel.Debug).Should().BeFalse(category + " must not log Debug");
            logger.IsEnabled(LogLevel.Warning).Should().BeTrue(category + " must still log Warning");
        }

        // Control: the same configuration does lower a category the floor does not cover.
        factory.CreateLogger("Decisya.Control").IsEnabled(LogLevel.Debug).Should().BeTrue();
    }

    private static Envelope EnvelopeWith(byte[] data) => new() { Data = data, ContentType = "application/json" };

    private static byte[] WithoutProperty(byte[] json, string name)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        var key = node.Select(pair => pair.Key).Single(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        node.Remove(key);
        return JsonSerializer.SerializeToUtf8Bytes(node);
    }

    private static byte[] WithProperty(byte[] json, string name, JsonNode? value)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        var key = node.Select(pair => pair.Key).Single(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        node[key] = value;
        return JsonSerializer.SerializeToUtf8Bytes(node);
    }
}
