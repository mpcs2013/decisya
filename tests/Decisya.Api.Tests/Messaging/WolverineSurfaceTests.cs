using System.Reflection;
using Decisya.Api.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace Decisya.Api.Tests.Messaging;

/// <summary>
/// Pins what <c>AddDecisyaMessaging</c> assumes about WolverineFx 6.39.1 (issue #231), so a Wolverine
/// bump that changes one fails here, with the reason, and not as a quiet loss of the stamp.
/// </summary>
[Trait("Category", "Unit")]
public class WolverineSurfaceTests
{
    [Fact]
    public async Task The_code_generation_mode_is_static_so_no_runtime_compiler_runs_in_the_Api_process()
    {
        await using var host = await MessagingHost.StartAsync("Production", probeHandlers: false);

        host.Runtime.Options.CodeGeneration.TypeLoadMode.Should().Be(JasperFx.CodeGeneration.TypeLoadMode.Static);
    }

    [Fact]
    public void Wolverine_registers_exactly_one_scoped_IMessageBus_and_it_is_replaced_by_the_stamping_wrapper()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddWolverine(ExtensionDiscovery.ManualOnly, _ => { });
        var wolverines = builder.Services.Where(d => d.ServiceType == typeof(IMessageBus)).ToList();
        wolverines.Should().ContainSingle().Which.Lifetime.Should().Be(ServiceLifetime.Scoped);

        var decorated = WebApplication.CreateBuilder();
        decorated.AddDecisyaMessaging();

        var descriptors = decorated.Services.Where(d => d.ServiceType == typeof(IMessageBus)).ToList();
        descriptors.Should().ContainSingle().Which.Lifetime.Should().Be(ServiceLifetime.Scoped);
        descriptors[0].ImplementationFactory.Should().NotBeNull();
    }

    [Fact]
    public void MediatorOnly_exists_in_the_pinned_version_and_is_what_the_host_sets()
    {
        Enum.IsDefined(DurabilityMode.MediatorOnly).Should().BeTrue();
    }

    // #326 G4-326-01 / R2 (a): a member Wolverine adds to IMessageBus or one of its base interfaces
    // that the stamp does not declare itself would be bound to something other than the stamp (a
    // default interface member), so a send path would skip the tenant stamp. Property accessors are
    // methods in the interface map, so they are walked too.
    [Fact]
    public void Every_member_of_IMessageBus_and_its_base_interfaces_is_declared_by_the_stamp_itself()
    {
        Type[] interfaces = [typeof(IMessageBus), .. typeof(IMessageBus).GetInterfaces()];
        var stamp = typeof(EnvelopeTenantStamp);
        var walked = 0;

        foreach (var contract in interfaces)
        {
            var map = stamp.GetInterfaceMap(contract);
            map.TargetMethods.Should().HaveSameCount(map.InterfaceMethods);
            foreach (var target in map.TargetMethods)
            {
                target.DeclaringType.Should().Be(stamp, contract.FullName + "." + target.Name + " must be declared by EnvelopeTenantStamp");
            }

            walked += map.InterfaceMethods.Length;
        }

        interfaces.Should().HaveCount(2, "IMessageBus has one base interface (ICommandBus) in 6.64.0: " + string.Join(", ", interfaces.Select(i => i.FullName)));
        walked.Should().Be(19, "the interface members walked (accessors included); a new IMessageBus member must be handled by the stamp (#326 R2)");
    }

    // #326 G4-326-03 (a): the extension methods a bus or context can call that Wolverine and JasperFx
    // define. Each is code that runs on the scope's bus and that this pin makes visible; a new one
    // needs a G3 look at whether it can reach a transport, a store or another tenant.
    [Fact]
    public void The_extension_methods_on_a_bus_or_context_type_in_Wolverine_and_JasperFx_are_the_pinned_list()
    {
        // #326: the same pin was run against WolverineFx 6.39.1 in a scratch copy of main (orchestrator,
        // 2026-11-19): the list was identical except BroadcastToAllNodesAsync, which is absent in 6.39.1
        // (refused on the stamp, see EnvelopeStampTests).
        string[] expected =
        [
            "Wolverine.MessageBusExtensions::System.Threading.Tasks.ValueTask BroadcastToAllNodesAsync[T](Wolverine.IMessageBus, T, Wolverine.DeliveryOptions)",
            "Wolverine.MessageBusExtensions::System.Threading.Tasks.ValueTask PublishAllAsync[T](Wolverine.IMessageBus, System.Collections.Generic.IEnumerable`1[T])",
            "Wolverine.MessageBusExtensions::System.Threading.Tasks.ValueTask ScheduleAsync[T](Wolverine.IMessageBus, T, System.DateTimeOffset, Wolverine.DeliveryOptions)",
            "Wolverine.MessageBusExtensions::System.Threading.Tasks.ValueTask ScheduleAsync[T](Wolverine.IMessageBus, T, System.TimeSpan, Wolverine.DeliveryOptions)",
            "Wolverine.Runtime.AgentMessagingExtensions::System.Threading.Tasks.Task FanOutToAllNodes(Wolverine.IMessageContext, System.Object)",
            "Wolverine.Runtime.AgentMessagingExtensions::System.Threading.Tasks.Task`1[System.Boolean] InvokeOnAgentOrForwardAsync(Wolverine.IMessageContext, System.Uri, System.Func`3[Wolverine.Runtime.IWolverineRuntime,System.Threading.CancellationToken,System.Threading.Tasks.Task], System.Threading.CancellationToken)",
            "Wolverine.Runtime.AgentMessagingExtensions::System.Threading.Tasks.Task`1[System.Boolean] InvokeOnAgentOrForwardAsync[T](Wolverine.IMessageContext, System.Uri, System.Func`2[T,System.Threading.Tasks.Task], System.Threading.CancellationToken)",
            "Wolverine.Runtime.AgentMessagingExtensions::System.Threading.Tasks.Task`1[Wolverine.Runtime.AgentInvocationOutcome] InvokeOnAgentAsync(Wolverine.IMessageContext, System.Uri, System.Func`3[Wolverine.Runtime.IWolverineRuntime,System.Threading.CancellationToken,System.Threading.Tasks.Task], System.Threading.CancellationToken)",
            "Wolverine.Runtime.AgentMessagingExtensions::System.Threading.Tasks.Task`1[Wolverine.Runtime.AgentInvocationOutcome] InvokeOnAgentAsync[T](Wolverine.IMessageContext, System.Uri, System.Func`2[T,System.Threading.Tasks.Task], System.Threading.CancellationToken)",
        ];

        var actual = ExtensionMethodsOnBusOrContext();

        actual.Should().Equal(
            expected,
            "the actual list, to copy into the expectation: {0}",
            "[" + Environment.NewLine + string.Join("," + Environment.NewLine, actual.Select(entry => "    \"" + entry + "\"")) + Environment.NewLine + "]");
    }

    // #326 G4-326-03 (b): what AddDecisyaMessaging adds to the container that is a bus, a context or a
    // hosted service. A new one is a new way to send or a new background worker.
    [Fact]
    public void The_bus_context_and_hosted_service_registrations_AddDecisyaMessaging_adds_are_the_pinned_list()
    {
        // #326 G6-326-02: duplicates are kept (no Distinct); the two IHostedService factory lines are
        // intended. The same pin run against WolverineFx 6.39.1 (scratch copy of main, orchestrator,
        // 2026-11-19) gave the identical 8 registrations.
        string[] expected =
        [
            "Microsoft.Extensions.Hosting.IHostedService | Singleton | factory",
            "Microsoft.Extensions.Hosting.IHostedService | Singleton | factory",
            "Wolverine.IMessageBus | Scoped | factory",
            "Wolverine.IMessageContext | Scoped | factory",
            "Wolverine.Runtime.Heartbeat.SoloHeartbeatService | Singleton | Wolverine.Runtime.Heartbeat.SoloHeartbeatService",
            "Wolverine.Runtime.IWolverineRuntime | Singleton | Wolverine.Runtime.WolverineRuntime",
            "Wolverine.Runtime.MessageContext | Scoped | Wolverine.Runtime.MessageContext",
            "Wolverine.Runtime.ScopedMessageContextHolder | Scoped | Wolverine.Runtime.ScopedMessageContextHolder",
        ];

        var builder = WebApplication.CreateBuilder();
        var before = new HashSet<ServiceDescriptor>(builder.Services, ReferenceEqualityComparer.Instance);
        builder.AddDecisyaMessaging();

        var actual = builder.Services
            .Where(descriptor => !before.Contains(descriptor))
            .Where(IsBusContextOrHostedService)
            .Select(Describe)
            .Order(StringComparer.Ordinal)
            .ToList();

        actual.Should().Equal(
            expected,
            "the actual list, to copy into the expectation: {0}",
            "[" + Environment.NewLine + string.Join("," + Environment.NewLine, actual.Select(entry => "    \"" + entry + "\"")) + Environment.NewLine + "]");
    }

    // #326 G6-326-02: the hosted services that really run on the composed Production host, by concrete
    // type. A new background worker (agent, heartbeat, listener) must show up here.
    [Fact]
    public async Task The_hosted_services_of_the_composed_Production_host_are_the_pinned_list()
    {
        // #326: the same 8 types were found on WolverineFx 6.39.1 (scratch copy of main, orchestrator,
        // 2026-11-19).
        string[] expected =
        [
            "Decisya.ServiceDefaults.Logging.EphemeralUserIdHashKeyWarning",
            "Microsoft.AspNetCore.DataProtection.Internal.DataProtectionHostedService",
            "Microsoft.AspNetCore.Hosting.GenericWebHostService",
            "Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckPublisherHostedService",
            "OpenTelemetry.Extensions.Hosting.Implementation.TelemetryHostedService",
            "Ostium.Authentication.Jwt.ApiJwtBearerStartupCheck",
            "Wolverine.Runtime.Heartbeat.SoloHeartbeatService",
            "Wolverine.Runtime.WolverineRuntime",
        ];

        await using var host = await MessagingHost.StartAsync("Production", probeHandlers: false);
        var actual = host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .Select(service => service.GetType().FullName ?? service.GetType().Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        actual.Should().Equal(
            expected,
            "the actual list, to copy into the expectation: {0}",
            "[" + Environment.NewLine + string.Join("," + Environment.NewLine, actual.Select(entry => "    \"" + entry + "\"")) + Environment.NewLine + "]");
    }

    private static bool IsBusType(Type type)
    {
        if (type.IsGenericParameter)
        {
            return type.GetGenericParameterConstraints().Any(IsBusType);
        }

        var name = type.Name;
        return typeof(IMessageBus).IsAssignableFrom(type)
            || name.Contains("MessageBus", StringComparison.Ordinal)
            || name.Contains("MessageContext", StringComparison.Ordinal)
            || name.Contains("MessageOutbox", StringComparison.Ordinal)
            || name.Contains("CommandBus", StringComparison.Ordinal);
    }

    private static bool IsBusContextOrHostedService(ServiceDescriptor descriptor)
    {
        if (descriptor.IsKeyedService)
        {
            return IsBusType(descriptor.ServiceType);
        }

        var implementation = descriptor.ImplementationType;
        return IsBusType(descriptor.ServiceType)
            || (implementation is not null && IsBusType(implementation))
            || typeof(Microsoft.Extensions.Hosting.IHostedService).IsAssignableFrom(descriptor.ServiceType)
            || (implementation is not null && typeof(Microsoft.Extensions.Hosting.IHostedService).IsAssignableFrom(implementation));
    }

    private static string Describe(ServiceDescriptor descriptor)
    {
        var implementation = descriptor.IsKeyedService
            ? "keyed"
            : descriptor.ImplementationType?.FullName
                ?? (descriptor.ImplementationFactory is not null ? "factory" : descriptor.ImplementationInstance?.GetType().FullName ?? "unknown");
        return descriptor.ServiceType.FullName + " | " + descriptor.Lifetime + " | " + implementation;
    }

    private static List<string> ExtensionMethodsOnBusOrContext()
    {
        var seen = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        var pending = new Queue<Assembly>([typeof(EnvelopeTenantStamp).Assembly, typeof(WolverineSurfaceTests).Assembly]);
        while (pending.Count > 0)
        {
            var assembly = pending.Dequeue();
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                var name = reference.Name;
                if (name is null || seen.ContainsKey(name)
                    || !(name == "Wolverine" || name.StartsWith("Wolverine.", StringComparison.Ordinal)
                        || name.StartsWith("JasperFx", StringComparison.Ordinal)))
                {
                    continue;
                }

                try
                {
                    var loaded = Assembly.Load(reference);
                    seen[name] = loaded;
                    pending.Enqueue(loaded);
                }
                catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    // A referenced assembly that is not in the output is not part of the surface.
                }
            }
        }

        var result = new List<string>();
        foreach (var assembly in seen.Values)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = exception.Types.Where(t => t is not null).Select(t => t!).ToArray();
            }

            foreach (var type in types.Where(t => t.IsVisible))
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (!method.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), inherit: false))
                    {
                        continue;
                    }

                    var first = method.GetParameters().FirstOrDefault();
                    if (first is not null && IsBusType(first.ParameterType))
                    {
                        result.Add(type.FullName + "::" + method);
                    }
                }
            }
        }

        return result.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }
}
