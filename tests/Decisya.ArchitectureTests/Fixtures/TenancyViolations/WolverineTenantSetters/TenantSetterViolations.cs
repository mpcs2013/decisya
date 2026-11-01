using Wolverine;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations.WolverineTenantSetters;

// R6 red fixtures (#231, ADR-0024 point 4; G3 G4-231-01, T-04): each names a Wolverine member whose name
// contains "Tenant" outside Decisya.Api.Messaging.EnvelopeTenantStamp. Any of them could forge another
// tenant's scope on the bus.

/// <summary>Sets the envelope's tenant (<c>set_TenantId</c>).</summary>
public static class SetsEnvelopeTenant
{
    public static void Set(Envelope envelope, string tenant)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        envelope.TenantId = tenant;
    }
}

/// <summary>Sets the tenant through a <c>DeliveryOptions</c> initializer.</summary>
public static class SetsDeliveryOptionsTenant
{
    public static DeliveryOptions Make(string tenant) => new() { TenantId = tenant };
}

/// <summary>Names another tenant with <c>InvokeForTenantAsync</c>.</summary>
public static class InvokesForAnotherTenant
{
    public static Task Send(IMessageBus bus, string tenant, object message)
    {
        ArgumentNullException.ThrowIfNull(bus);
        return bus.InvokeForTenantAsync(tenant, message);
    }
}

/// <summary>Sets the bus's own <c>TenantId</c>.</summary>
public static class SetsBusTenant
{
    public static void Set(IMessageBus bus, string tenant)
    {
        ArgumentNullException.ThrowIfNull(bus);
        bus.TenantId = tenant;
    }
}

/// <summary>Reads <c>get_TenantId</c> in a type that is not the stamp or the receive middleware.</summary>
public static class ReadsEnvelopeTenantOutsideTheReceiveMiddleware
{
    public static string? Read(Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return envelope.TenantId;
    }
}
