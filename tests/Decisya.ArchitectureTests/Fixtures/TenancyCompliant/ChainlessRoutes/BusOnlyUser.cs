using Wolverine;

namespace Decisya.ArchitectureTests.Fixtures.TenancyCompliant.ChainlessRoutes;

/// <summary>R17 green fixture (#231): takes and calls only <c>Wolverine.IMessageBus</c>.</summary>
public sealed class BusOnlyUser(IMessageBus bus)
{
    public Task SendAsync(object message, CancellationToken cancellation) => bus.InvokeAsync(message, cancellation);
}
