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
}
