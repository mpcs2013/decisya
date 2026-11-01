using Decisya.Api.Authentication;
using Wolverine;

namespace Decisya.Api.Messaging;

/// <summary>
/// Puts <see cref="EnvelopeTenantStamp"/> in front of Wolverine's scoped message bus: the
/// <see cref="IMessageBus"/> a scope resolves is the stamping wrapper around the bus Wolverine's own
/// registration builds, paired with that scope's <see cref="RequestCaller"/>. Nothing is cached: the
/// wrapper reads the caller on every call (ADR-0024 point 4).
/// </summary>
/// <remarks>
/// Wolverine also registers its concrete scoped bus as <c>IMessageContext</c> and <c>MessageContext</c>,
/// and its own <c>IMessageBus</c> factory resolves <c>MessageContext</c>, so those two cannot be
/// removed. Neither carries the stamp: a type that takes either (or <c>MessageBus</c>) can invoke
/// without a tenant, which the receive middleware then refuses as <c>missing</c>, or name another
/// tenant through Wolverine's own members, which architecture rule R6 forbids outside the stamp. A
/// further rule (no Api type references them) is left to G5 (reported at G4).
/// </remarks>
internal static class MessageCallerBindings
{
    internal static void DecorateScopedBus(IServiceCollection services)
    {
        var original = services.LastOrDefault(d => d.ServiceType == typeof(IMessageBus) && d.Lifetime == ServiceLifetime.Scoped)
            ?? throw new InvalidOperationException(
                "Wolverine no longer registers IMessageBus as a scoped service; the envelope tenant stamp cannot wrap it.");

        services.Remove(original);
        services.AddScoped<IMessageBus>(serviceProvider =>
        {
            var bus = original.ImplementationFactory is not null
                ? (IMessageBus)original.ImplementationFactory(serviceProvider)
                : (IMessageBus)ActivatorUtilities.CreateInstance(serviceProvider, original.ImplementationType!);
            return new EnvelopeTenantStamp(bus, serviceProvider.GetRequiredService<RequestCaller>());
        });
    }
}
