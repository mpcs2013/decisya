using Decisya.Api.Authentication;
using Decisya.Api.Messaging;
using Decisya.SharedKernel.Tenancy;
using Wolverine;

namespace Decisya.Api.Tests.Messaging;

/// <summary>An <see cref="IMessageBus"/> that records every call and the options it received.</summary>
internal sealed class RecordingBus : IMessageBus
{
    public List<string> Calls { get; } = [];

    public List<DeliveryOptions?> ReceivedOptions { get; } = [];

    public string? TenantId { get; set; }

    private void Record(string call, DeliveryOptions? options)
    {
        Calls.Add(call);
        ReceivedOptions.Add(options);
    }

    public Task InvokeAsync(object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        Record(nameof(InvokeAsync), null);
        return Task.CompletedTask;
    }

    public Task InvokeAsync(object message, DeliveryOptions options, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        Record(nameof(InvokeAsync), options);
        return Task.CompletedTask;
    }

    public Task<T> InvokeAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        Record(nameof(InvokeAsync), null);
        return Task.FromResult(default(T)!);
    }

    public Task<T> InvokeAsync<T>(object message, DeliveryOptions options, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        Record(nameof(InvokeAsync), options);
        return Task.FromResult(default(T)!);
    }

    public IAsyncEnumerable<TResponse> StreamAsync<TResponse>(object message, CancellationToken cancellation = default)
    {
        Record(nameof(StreamAsync), null);
        return AsyncEnumerable.Empty<TResponse>();
    }

    public IAsyncEnumerable<TResponse> StreamAsync<TResponse>(object message, DeliveryOptions options, CancellationToken cancellation = default)
    {
        Record(nameof(StreamAsync), options);
        return AsyncEnumerable.Empty<TResponse>();
    }

    public Task<TResponse> StreamAsync<TRequest, TResponse>(IAsyncEnumerable<TRequest> messages, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        Record(nameof(StreamAsync), null);
        return Task.FromResult(default(TResponse)!);
    }

    public Task<TResponse> StreamAsync<TRequest, TResponse>(IAsyncEnumerable<TRequest> messages, DeliveryOptions options, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        Record(nameof(StreamAsync), options);
        return Task.FromResult(default(TResponse)!);
    }

    public ValueTask SendAsync<T>(T message, DeliveryOptions? options = null)
    {
        Record(nameof(SendAsync), options);
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishAsync<T>(T message, DeliveryOptions? options = null)
    {
        Record(nameof(PublishAsync), options);
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<Envelope> PreviewSubscriptions(object message)
    {
        Record(nameof(PreviewSubscriptions), null);
        return [];
    }

    public IReadOnlyList<Envelope> PreviewSubscriptions(object message, DeliveryOptions options)
    {
        Record(nameof(PreviewSubscriptions), options);
        return [];
    }

    public Task InvokeForTenantAsync(string tenantId, object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        Record(nameof(InvokeForTenantAsync), null);
        return Task.CompletedTask;
    }

    public Task<T> InvokeForTenantAsync<T>(string tenantId, object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        Record(nameof(InvokeForTenantAsync), null);
        return Task.FromResult(default(T)!);
    }

    public IDestinationEndpoint EndpointFor(string endpointName)
    {
        Record(nameof(EndpointFor), null);
        throw new NotSupportedException();
    }

    public IDestinationEndpoint EndpointFor(Uri uri)
    {
        Record(nameof(EndpointFor), null);
        throw new NotSupportedException();
    }

    public ValueTask BroadcastToTopicAsync(string topicName, object message, DeliveryOptions? options = null)
    {
        Record(nameof(BroadcastToTopicAsync), options);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Issue #231, G3 S-10: the stamp refuses before Wolverine sees anything (zero inner calls without an
/// ambient tenant, on every overload), and what the inner bus does receive is already stamped.
/// </summary>
[Trait("Category", "Unit")]
public class EnvelopeStampInnerBusTests
{
    private static readonly TenantId TenantA = TenantId.Parse("018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f70");
    private static readonly TenantId TenantB = TenantId.Parse("018f2c9e-6b7a-7c3e-8b1a-2f3c4d5e6f71");

    [Fact]
    public async Task With_no_ambient_caller_the_inner_bus_receives_zero_calls_on_every_overload()
    {
        var inner = new RecordingBus();
        var stamp = new EnvelopeTenantStamp(inner, new RequestCaller());

        await AssertEveryCallThrowsTheStampsExceptionAsync(stamp);

        inner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task With_an_ambient_that_has_no_tenant_the_inner_bus_receives_zero_calls_on_every_overload()
    {
        var inner = new RecordingBus();
        var caller = new RequestCaller();
        caller.Set(TenantResolution.NoTenant, "platform-admin-user", isPlatformAdmin: true);
        var stamp = new EnvelopeTenantStamp(inner, caller);

        await AssertEveryCallThrowsTheStampsExceptionAsync(stamp);

        inner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task With_a_tenant_ambient_every_overload_reaches_the_inner_bus_with_the_options_already_stamped()
    {
        var inner = new RecordingBus();
        var caller = new RequestCaller();
        caller.Set(TenantResolution.For(TenantA), "user-a", isPlatformAdmin: false);
        var stamp = new EnvelopeTenantStamp(inner, caller);
        var calls = BusCalls.All("inner", TestContext.Current.CancellationToken);

        foreach (var (_, call) in calls)
        {
            await call(stamp);
        }

        inner.Calls.Should().HaveCount(calls.Count);
        inner.ReceivedOptions.Should().OnlyContain(options => options != null, "every overload hands Wolverine options, never none");
        foreach (var options in inner.ReceivedOptions)
        {
            options!.TenantId.Should().Be(TenantA.ToString());
            options.Headers[EnvelopeFacts.CallerIdHeader].Should().Be("user-a");
        }
    }

    [Fact]
    public async Task A_foreign_tenant_and_caller_in_the_options_are_already_overwritten_when_the_inner_bus_sees_them()
    {
        var inner = new RecordingBus();
        var caller = new RequestCaller();
        caller.Set(TenantResolution.For(TenantA), "user-a", isPlatformAdmin: false);
        var stamp = new EnvelopeTenantStamp(inner, caller);
        var foreign = new DeliveryOptions { TenantId = TenantB.ToString() }.WithHeader(EnvelopeFacts.CallerIdHeader, "forged");

        await stamp.InvokeAsync(new ProbeMessage("foreign"), foreign, TestContext.Current.CancellationToken);

        inner.ReceivedOptions.Should().ContainSingle().Which!.TenantId.Should().Be(TenantA.ToString());
        inner.ReceivedOptions[0]!.Headers[EnvelopeFacts.CallerIdHeader].Should().Be("user-a");
    }

    [Fact]
    public async Task The_calls_that_name_another_tenant_or_a_transport_never_reach_the_inner_bus()
    {
        var inner = new RecordingBus();
        var caller = new RequestCaller();
        caller.Set(TenantResolution.For(TenantA), "user-a", isPlatformAdmin: false);
        var stamp = new EnvelopeTenantStamp(inner, caller);
        var token = TestContext.Current.CancellationToken;

        var calls = new Func<Task>[]
        {
            () => stamp.InvokeForTenantAsync(TenantB.ToString(), new ProbeMessage("x"), token),
            () => stamp.InvokeForTenantAsync<ProbeAnswer>(TenantB.ToString(), new ProbeQuery("x"), token),
            () =>
            {
                _ = stamp.EndpointFor("x");
                return Task.CompletedTask;
            },
            () =>
            {
                _ = stamp.EndpointFor(new Uri("local://x"));
                return Task.CompletedTask;
            },
            () => stamp.BroadcastToTopicAsync("x", new ProbeMessage("x")).AsTask(),
        };
        foreach (var call in calls)
        {
            await call.Should().ThrowAsync<NotSupportedException>();
        }

        inner.Calls.Should().BeEmpty();
    }

    private static async Task AssertEveryCallThrowsTheStampsExceptionAsync(IMessageBus stamp)
    {
        foreach (var (name, call) in BusCalls.All("no-ambient", TestContext.Current.CancellationToken))
        {
            var act = () => call(stamp);

            var thrown = await act.Should().ThrowAsync<InvalidOperationException>(name + " must throw");
            thrown.Which.Message.Should().Contain("ambient tenant and caller", name + " must throw the stamp's own exception");
        }
    }
}
