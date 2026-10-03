using Decisya.ServiceDefaults.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

namespace Decisya.ServiceDefaults.Tests.Telemetry;

/// <summary>Collection for tests that change the process-wide default text-map propagator.
/// Parallelization is disabled so no other test observes the temporary value.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class GlobalPropagatorCollectionDefinition
{
    public const string Name = "Global propagator";
}

/// <summary>Issue #27, B-01: <c>UseInboundTraceRoots</c> is opt-in and process-wide.</summary>
[Collection(GlobalPropagatorCollectionDefinition.Name)]
public sealed class InboundTraceRootsTests : IDisposable
{
    private const string TraceId = "0af7651916cd43dd8448eb211c80319c";
    private const string Traceparent = $"00-{TraceId}-b7ad6b7169203331-01";

    private readonly TextMapPropagator _original = Propagators.DefaultTextMapPropagator;

    public void Dispose() => Sdk.SetDefaultTextMapPropagator(_original);

    [Fact]
    public void UseInboundTraceRoots_sets_a_trace_context_only_propagator_that_drops_baggage()
    {
        Sdk.SetDefaultTextMapPropagator(new CompositeTextMapPropagator(
            [new TraceContextPropagator(), new BaggagePropagator()]));
        var builder = NewBuilder();

        builder.UseInboundTraceRoots();

        Propagators.DefaultTextMapPropagator.Should().BeOfType<TraceContextPropagator>();
        var extracted = Extract();
        extracted.ActivityContext.TraceId.ToHexString().Should().Be(TraceId);
        extracted.Baggage.Count.Should().Be(0);
    }

    [Fact]
    public void AddServiceDefaults_alone_leaves_the_default_composite_propagator_unchanged()
    {
        // Normalise to the SDK default so the assertion does not depend on test order.
        Sdk.SetDefaultTextMapPropagator(new CompositeTextMapPropagator(
            [new TraceContextPropagator(), new BaggagePropagator()]));
        var before = Propagators.DefaultTextMapPropagator;

        NewBuilder();

        Propagators.DefaultTextMapPropagator.Should().BeSameAs(before);
        Propagators.DefaultTextMapPropagator.Should().BeOfType<CompositeTextMapPropagator>();
        Propagators.DefaultTextMapPropagator.Fields.Should().Contain(["traceparent", "baggage"]);
        var extracted = Extract();
        extracted.ActivityContext.TraceId.ToHexString().Should().Be(TraceId);
        extracted.Baggage.Count.Should().Be(1);
    }

    private static HostApplicationBuilder NewBuilder()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Development",
        });
        builder.Configuration.AddInMemoryCollection(
        [
            new(DecisyaObservabilityOptions.UserIdHashKeyPath, Canaries.HashKey()),
        ]);
        builder.AddServiceDefaults();
        return builder;
    }

    private static PropagationContext Extract()
    {
        var headers = new Dictionary<string, string>
        {
            ["traceparent"] = Traceparent,
            ["baggage"] = "k=v",
        };
        return Propagators.DefaultTextMapPropagator.Extract(
            default,
            headers,
            static (carrier, key) => carrier.TryGetValue(key, out var value) ? [value] : []);
    }
}
