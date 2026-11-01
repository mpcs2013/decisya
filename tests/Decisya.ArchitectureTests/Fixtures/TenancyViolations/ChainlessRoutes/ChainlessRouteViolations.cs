using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.RemoteInvocation;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations.ChainlessRoutes;

// R17 red fixtures (#231, G3 S-12): one per route into Wolverine's chain-less handlers.

/// <summary>A type Wolverine routes to <c>AgentCommandHandler</c>, by interface.</summary>
public sealed class ImplementsAgentCommand : IAgentCommand
{
    public Guid? DestinationNodeId => null;

    public Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken) =>
        Task.FromResult<AgentCommands>(null!);
}

/// <summary>Creates a <c>Wolverine.Envelope</c> (routed to <c>ScheduledSendEnvelopeHandler</c>).</summary>
public static class CreatesEnvelope
{
    public static Envelope Make() => new(new object());
}

/// <summary>Creates a <c>Wolverine.Runtime.RemoteInvocation.Acknowledgement</c>.</summary>
public static class CreatesAcknowledgement
{
    public static Acknowledgement Make() => new();
}

/// <summary>Creates a <c>Wolverine.Runtime.RemoteInvocation.FailureAcknowledgement</c>.</summary>
public static class CreatesFailureAcknowledgement
{
    public static FailureAcknowledgement Make() => new();
}
