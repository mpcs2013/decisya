using Wolverine;

// Deliberately in the namespace G2 first proposed as R14's exemption (S-09 narrowed it to exact type names):
// declared in this fixture assembly, and in Decisya.Api's own namespace, it must still be red.
namespace Decisya.Api.Messaging;

/// <summary>R14 red fixture: a type in <c>Decisya.Api.Messaging</c> that takes an unstamped context.</summary>
public sealed class MisnamedBusUser(IMessageContext context)
{
    public IMessageContext Context { get; } = context;
}
