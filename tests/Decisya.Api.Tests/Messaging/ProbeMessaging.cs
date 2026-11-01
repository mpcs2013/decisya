using System.Collections.Concurrent;
using Decisya.Api.Messaging;
using Decisya.ServiceDefaults.Logging;
using Decisya.SharedKernel.Tenancy;
using Wolverine;

namespace Decisya.Api.Tests.Messaging;

// Test messages and real handlers for the envelope-tenant tests (issue #231). The handlers are
// compiled into real Wolverine chains: WolverineFx.RuntimeCompilation is referenced by this test
// project only (ADR-0024 amendment 1 item 5), and the host under test switches TypeLoadMode to
// Dynamic and nothing else. They record what they saw inside the handler: the envelope, the ambient
// tenant and caller, the log enrichment, and the tenant a stand-in DbContext saw when it was built.

public sealed record ProbeMessage(string Label);

public sealed record ProbeInner(string Label);

public sealed record ProbeStaticMessage(string Label);

public sealed record ProbeQuery(string Label);

public sealed record ProbeAnswer(string Label);

public sealed record ProbeCascadeMessage(string Label);

public sealed record ProbeCascaded(string Label);

public sealed record ProbeStreamQuery(string Label);

public sealed record ProbeObservation(
    string Handler,
    string Label,
    string? EnvelopeTenant,
    string? CallerHeader,
    TenantResolution Tenant,
    string? UserId,
    bool IsPlatformAdmin,
    string? EnrichedTenant,
    string? EnrichedUserHash,
    TenantResolution TenantSeenByDb,
    TenantResolution TenantAtUse,
    string? CallerAtUse,
    bool DbSharesTheHandlersTenantInstance);

/// <summary>Singleton: where the probe handlers write what they saw, and the gate they wait on.</summary>
public sealed class ProbeRecorder
{
    private int _arrived;
    private int _useReads;

    /// <summary>How many times a dependency read the tenant or caller at use time.</summary>
    public int UseReads => Volatile.Read(ref _useReads);

    public void UseRead() => Interlocked.Increment(ref _useReads);

    /// <summary>
    /// Each <see cref="ProbeDb"/> built: the scope's <see cref="ICurrentTenant"/> it was handed and the
    /// resolution that instance showed at that moment. Lets a refused message show what its scope
    /// holds afterwards.
    /// </summary>
    public ConcurrentQueue<(ICurrentTenant Instance, TenantResolution AtConstruction)> ConstructedTenants { get; } = new();

    public ConcurrentQueue<ProbeObservation> Observed { get; } = new();

    /// <summary>Every handler and dependency constructed, by type name.</summary>
    public ConcurrentQueue<string> Constructed { get; } = new();

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Arrived => Volatile.Read(ref _arrived);

    public void Arrive() => Interlocked.Increment(ref _arrived);
}

/// <summary>
/// Stands in for a tenant-dependent service built the way WolverineFx 6.39.1 builds it: scoped, and
/// constructed at the top of the generated chain, before the envelope tenant middleware runs. It
/// records the tenant it sees at construction (always <c>Invalid</c>: the control allows
/// construction), keeps the <see cref="ICurrentTenant"/> and <see cref="ICurrentCaller"/> references,
/// and reads them only in <see cref="Use"/>, the approved use-time pattern (rule R16). Every
/// <see cref="Use"/> is counted, so a refused message can show that nothing was read.
/// </summary>
public sealed class ProbeDb
{
    private readonly ICurrentCaller _caller;
    private readonly ProbeRecorder _recorder;

    public ProbeDb(ICurrentTenant tenant, ICurrentCaller caller, ProbeRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(recorder);

        Tenant = tenant;
        _caller = caller;
        _recorder = recorder;
        TenantSeenAtConstruction = tenant.Resolution;
        recorder.Constructed.Enqueue(nameof(ProbeDb));
        recorder.ConstructedTenants.Enqueue((tenant, TenantSeenAtConstruction));
    }

    /// <summary>The very <see cref="ICurrentTenant"/> instance the scope handed this dependency.</summary>
    public ICurrentTenant Tenant { get; }

    public TenantResolution TenantSeenAtConstruction { get; }

    /// <summary>The use-time read: tenant and caller, through the kept references.</summary>
    public (TenantResolution Tenant, string CallerId) Use()
    {
        _recorder.UseRead();
        return (Tenant.Resolution, _caller.UserId);
    }
}

public sealed class ProbeMessageHandler
{
    private readonly ProbeRecorder _recorder;
    private readonly ProbeDb _db;

    public ProbeMessageHandler(ProbeRecorder recorder, ProbeDb db)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(db);

        _recorder = recorder;
        _db = db;
        recorder.Constructed.Enqueue(nameof(ProbeMessageHandler));
    }

    public async Task Handle(
        ProbeMessage message,
        Envelope envelope,
        ICurrentTenant tenant,
        ICurrentCaller caller,
        ILogEnrichmentContext enrichment,
        IMessageContext context,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);

        // A label starting "gate" holds every handler until the test releases them all at once.
        if (message.Label.StartsWith("gate", StringComparison.Ordinal))
        {
            _recorder.Arrive();
            await _recorder.Release.Task.ConfigureAwait(false);
        }

        _recorder.Observed.Enqueue(ProbeObserve.From(nameof(ProbeMessageHandler), message.Label, envelope, tenant, caller, enrichment, _db));

        // A label starting "nested": the handler invokes a second message through its own context, the
        // way a handler-originated send reaches the bus.
        if (message.Label.StartsWith("nested", StringComparison.Ordinal))
        {
            await context.InvokeAsync(new ProbeInner(message.Label + "-inner"), cancellation).ConfigureAwait(false);
        }
    }
}

/// <summary>A static handler that takes its scoped dependency as a method parameter.</summary>
public static class ProbeStaticMessageHandler
{
    public static void Handle(
        ProbeStaticMessage message,
        Envelope envelope,
        ICurrentTenant tenant,
        ICurrentCaller caller,
        ILogEnrichmentContext enrichment,
        ProbeDb db,
        ProbeRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(recorder);

        recorder.Observed.Enqueue(ProbeObserve.From(nameof(ProbeStaticMessageHandler), message.Label, envelope, tenant, caller, enrichment, db));
    }
}

public sealed class ProbeInnerHandler(ProbeRecorder recorder)
{
    public void Handle(
        ProbeInner message, Envelope envelope, ICurrentTenant tenant, ICurrentCaller caller, ILogEnrichmentContext enrichment)
    {
        ArgumentNullException.ThrowIfNull(message);

        recorder.Observed.Enqueue(ProbeObserve.From(nameof(ProbeInnerHandler), message.Label, envelope, tenant, caller, enrichment, null));
    }
}

public sealed class ProbeQueryHandler(ProbeRecorder recorder)
{
    public ProbeAnswer Handle(ProbeQuery query, Envelope envelope, ICurrentTenant tenant, ICurrentCaller caller, ILogEnrichmentContext enrichment)
    {
        ArgumentNullException.ThrowIfNull(query);

        recorder.Observed.Enqueue(ProbeObserve.From(nameof(ProbeQueryHandler), query.Label, envelope, tenant, caller, enrichment, null));
        return new ProbeAnswer(query.Label);
    }
}

/// <summary>Returns a message that is not the invoke's response: a cascade.</summary>
public sealed class ProbeCascadeMessageHandler(ProbeRecorder recorder)
{
    public ProbeCascaded Handle(
        ProbeCascadeMessage message, Envelope envelope, ICurrentTenant tenant, ICurrentCaller caller, ILogEnrichmentContext enrichment)
    {
        ArgumentNullException.ThrowIfNull(message);

        recorder.Observed.Enqueue(ProbeObserve.From(nameof(ProbeCascadeMessageHandler), message.Label, envelope, tenant, caller, enrichment, null));
        return new ProbeCascaded(message.Label + "-cascaded");
    }
}

public sealed class ProbeCascadedHandler(ProbeRecorder recorder)
{
    public void Handle(
        ProbeCascaded message, Envelope envelope, ICurrentTenant tenant, ICurrentCaller caller, ILogEnrichmentContext enrichment)
    {
        ArgumentNullException.ThrowIfNull(message);

        recorder.Observed.Enqueue(ProbeObserve.From(nameof(ProbeCascadedHandler), message.Label, envelope, tenant, caller, enrichment, null));
    }
}

public sealed class ProbeStreamQueryHandler(ProbeRecorder recorder)
{
    public async IAsyncEnumerable<ProbeAnswer> Handle(
        ProbeStreamQuery query, Envelope envelope, ICurrentTenant tenant, ICurrentCaller caller, ILogEnrichmentContext enrichment)
    {
        ArgumentNullException.ThrowIfNull(query);

        recorder.Observed.Enqueue(ProbeObserve.From(nameof(ProbeStreamQueryHandler), query.Label, envelope, tenant, caller, enrichment, null));
        yield return new ProbeAnswer(query.Label);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}

internal static class ProbeObserve
{
    internal static ProbeObservation From(
        string handler,
        string label,
        Envelope envelope,
        ICurrentTenant tenant,
        ICurrentCaller caller,
        ILogEnrichmentContext enrichment,
        ProbeDb? db)
    {
        string? header = null;
        if (envelope.Headers.TryGetValue(EnvelopeFacts.CallerIdHeader, out var value))
        {
            header = value;
        }

        var use = db?.Use();

        return new ProbeObservation(
            handler,
            label,
            envelope.TenantId,
            header,
            tenant.Resolution,
            caller.UserId,
            caller.IsPlatformAdmin,
            enrichment.TenantId,
            enrichment.UserIdHash,
            db?.TenantSeenAtConstruction ?? default,
            use?.Tenant ?? default,
            use?.CallerId,
            db is not null && ReferenceEquals(db.Tenant, tenant));
    }
}

internal static class ProbeHandlers
{
    /// <summary>The handler types the test host lists, the way a module lists its own.</summary>
    internal static IReadOnlyList<Type> Types { get; } =
    [
        typeof(ProbeMessageHandler),
        typeof(ProbeStaticMessageHandler),
        typeof(ProbeInnerHandler),
        typeof(ProbeQueryHandler),
        typeof(ProbeCascadeMessageHandler),
        typeof(ProbeCascadedHandler),
        typeof(ProbeStreamQueryHandler),
    ];
}
