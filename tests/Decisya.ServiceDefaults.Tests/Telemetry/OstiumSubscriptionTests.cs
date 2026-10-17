using System.Diagnostics;
using Decisya.ServiceDefaults.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Decisya.ServiceDefaults.Tests.Telemetry;

/// <summary>ADR-0019 / issue #164 D3: <c>AddServiceDefaults</c> subscribes to the
/// <c>Ostium.*</c> trace sources by name, with no package reference. Ostium 0.1.1 declares
/// no source; this proves the subscription is ready for a later release.</summary>
[Collection(RealAspNetCoreHostCollectionDefinition.Name)]
public class OstiumSubscriptionTests
{
    private static readonly ActivitySource Subscribed = new("Ostium.Authentication.Jwt");
    private static readonly ActivitySource NotSubscribed = new("OstiumFoo.Authentication.Jwt");

    [Fact]
    public async Task An_Ostium_source_reaches_the_tracer_and_a_name_without_the_dot_does_not()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Development",
        });
        builder.Configuration.AddInMemoryCollection(
        [
            new(DecisyaObservabilityOptions.UserIdHashKeyPath, Canaries.HashKey()),
        ]);
        builder.AddServiceDefaults();

        using var host = builder.Build();
        await host.StartAsync(cancellationToken);

        using (var activity = Subscribed.StartActivity("ostium.test"))
        {
            activity.Should().NotBeNull("the Ostium.* wildcard is subscribed");
        }

        using (var activity = NotSubscribed.StartActivity("ostiumfoo.test"))
        {
            activity.Should().BeNull("OstiumFoo does not match the Ostium. prefix");
        }

        await host.StopAsync(cancellationToken);
    }
}
