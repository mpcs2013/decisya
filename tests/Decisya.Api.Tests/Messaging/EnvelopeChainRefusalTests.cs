using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Runtime.Handlers;

namespace Decisya.Api.Tests.Messaging;

/// <summary>
/// Issue #231, G3 G4-231-07: every generated chain refuses a message that carries no tenant, so the
/// receive middleware is in every chain and runs before the handler, shown by behaviour (what the
/// generated code does), not by the order of Wolverine's frame list.
/// </summary>
[Trait("Category", "Unit")]
[Collection(ChainHostDefinition.Name)]
public class EnvelopeChainRefusalTests(ChainHostFixture fixture)
{
    [Fact]
    public async Task Every_generated_chain_refuses_a_message_with_no_tenant_before_its_handler_runs()
    {
        var host = fixture.Host!;
        var graph = host.Services.GetRequiredService<HandlerGraph>();
        var chained = graph.AllChains().Select(chain => chain.MessageType).Distinct().ToList();
        chained.Should().NotBeEmpty();

        foreach (var messageType in chained)
        {
            var label = "chain-each-" + messageType.Name;
            await using var scope = host.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<IMessageContext>();
            var message = Activator.CreateInstance(messageType, label)!;
            var logsBefore = host.Logs.Records.Count;

            await context.InvokeAsync(message, new DeliveryOptions(), TestContext.Current.CancellationToken);

            host.Recorder.Observed.Any(o => o.Label == label).Should().BeFalse(messageType.Name + "'s handler must not run for a tenant-less message");
            host.Logs.Records.Skip(logsBefore).Count(r => r.EventName == "EnvelopeRefused" && r.StateText.Contains("Reason=missing", StringComparison.Ordinal))
                .Should().Be(1, messageType.Name + "'s chain must refuse it once, as missing");
        }
    }
}
