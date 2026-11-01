using Wolverine;

namespace Decisya.Api.Tests.Messaging;

/// <summary>Every call the stamping bus offers that carries a message, one entry per overload.</summary>
internal static class BusCalls
{
    internal static IReadOnlyList<(string Name, Func<IMessageBus, Task> Call)> All(string label, CancellationToken token)
    {
        var message = new ProbeMessage(label);
        var query = new ProbeQuery(label);
        var stream = new ProbeStreamQuery(label);

        return
        [
            ("InvokeAsync", bus => bus.InvokeAsync(message, token)),
            ("InvokeAsync+options", bus => bus.InvokeAsync(message, new DeliveryOptions(), token)),
            ("InvokeAsync<T>", bus => bus.InvokeAsync<ProbeAnswer>(query, token)),
            ("InvokeAsync<T>+options", bus => bus.InvokeAsync<ProbeAnswer>(query, new DeliveryOptions(), token)),
            ("StreamAsync", bus =>
            {
                _ = bus.StreamAsync<ProbeAnswer>(stream, token);
                return Task.CompletedTask;
            }),
            ("StreamAsync+options", bus =>
            {
                _ = bus.StreamAsync<ProbeAnswer>(stream, new DeliveryOptions(), token);
                return Task.CompletedTask;
            }),
            ("StreamAsync(requests)", bus => bus.StreamAsync<ProbeStreamQuery, ProbeAnswer>(Requests(), token)),
            ("StreamAsync(requests)+options", bus => bus.StreamAsync<ProbeStreamQuery, ProbeAnswer>(Requests(), new DeliveryOptions(), token)),
            ("SendAsync", bus => bus.SendAsync(message).AsTask()),
            ("SendAsync+options", bus => bus.SendAsync(message, new DeliveryOptions()).AsTask()),
            ("PublishAsync", bus => bus.PublishAsync(message).AsTask()),
            ("PublishAsync+options", bus => bus.PublishAsync(message, new DeliveryOptions()).AsTask()),
            ("PreviewSubscriptions", bus =>
            {
                _ = bus.PreviewSubscriptions(message);
                return Task.CompletedTask;
            }),
            ("PreviewSubscriptions+options", bus =>
            {
                _ = bus.PreviewSubscriptions(message, new DeliveryOptions());
                return Task.CompletedTask;
            }),
        ];
    }

    private static async IAsyncEnumerable<ProbeStreamQuery> Requests()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}
