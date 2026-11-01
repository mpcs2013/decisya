using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Runtime;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations.UnstampedBus;

// R14 red fixtures (#231, ADR-0024 amendment 1 item 3; G3 S-09): one per unstamped way to a bus. Each
// bypasses EnvelopeTenantStamp, so it can invoke a message with no tenant, or with a caller id of its choice.

/// <summary>A constructor that takes <c>Wolverine.IMessageContext</c>.</summary>
public sealed class TakesMessageContext(IMessageContext context)
{
    public IMessageContext Context { get; } = context;
}

/// <summary>A field of type <c>Wolverine.Runtime.MessageContext</c>.</summary>
public sealed class HoldsMessageContextField
{
    private MessageContext? _context;

    public bool HasContext => _context is not null;

    public void Keep(object candidate) => _context = candidate as MessageContext;
}

/// <summary>
/// A <c>Wolverine.Runtime.MessageBus</c> parameter. G2 proposed <c>new MessageBus(runtime)</c>, with a parameter
/// as the fallback when the constructor is not public; the parameter works either way.
/// </summary>
public static class TakesMessageBus
{
    public static void Use(MessageBus bus) => ArgumentNullException.ThrowIfNull(bus);
}

/// <summary>A call to the extension that builds a bus from a host: <c>host.MessageBus()</c>.</summary>
public static class CallsHostMessageBus
{
    public static void Use(IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _ = host.MessageBus();
    }
}
