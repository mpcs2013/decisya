using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Decisya.Modules.Audit.Contracts;
using Decisya.Modules.Entitlements.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Decisya.Modules.Entitlements.Tests.TestSupport;

/// <summary>A deliberately injected failure (G4-24-02 fault matrix). Its message never carries command data.</summary>
internal sealed class InjectedFaultException(string message) : Exception(message);

/// <summary>
/// Test seams for the NFR-37 fault matrix. They hang off <see cref="EntitlementsHarness"/>'s
/// <c>configure</c> hook, so the handlers run exactly as the API wires them, in a real DI scope.
/// </summary>
internal static class AuditFaults
{
    /// <summary>Wraps the real <see cref="IAuditWriter"/> (the module's internal type, created by the container) in a decorator.</summary>
    public static Action<IServiceCollection> Decorate(Func<IAuditWriter, IAuditWriter> decorator) => services =>
    {
        var original = services.Single(d => d.ServiceType == typeof(IAuditWriter));
        var implementation = original.ImplementationType
            ?? throw new InvalidOperationException("The audit writer registration has no implementation type.");
        services.Remove(original);
        services.AddScoped<IAuditWriter>(sp => decorator((IAuditWriter)ActivatorUtilities.CreateInstance(sp, implementation)));
    };

    /// <summary>Adds an interceptor to the handlers' own <c>EntitlementsDbContext</c> options (a second <c>AddDbContext</c> call merges).</summary>
    public static Action<IServiceCollection> Intercept(params IInterceptor[] interceptors) => services =>
        services.AddDbContext<EntitlementsDbContext>(options => options.AddInterceptors(interceptors));

    public static Action<IServiceCollection> All(params Action<IServiceCollection>[] configurations) => services =>
    {
        foreach (var configuration in configurations)
        {
            configuration(services);
        }
    };
}

/// <summary>Runs the real append, then throws: "something fails after the append succeeded".</summary>
internal sealed class ThrowAfterAppendWriter(IAuditWriter inner) : IAuditWriter
{
    public async Task AppendAsync(AuditEntry entry, DbTransaction transaction, CancellationToken cancellationToken = default)
    {
        await inner.AppendAsync(entry, transaction, cancellationToken);
        throw new InjectedFaultException("Injected fault after a real audit append.");
    }
}

/// <summary>Passes through, counting how many appends really ran.</summary>
internal sealed class CountingWriter(IAuditWriter inner, Counter counter) : IAuditWriter
{
    public async Task AppendAsync(AuditEntry entry, DbTransaction transaction, CancellationToken cancellationToken = default)
    {
        await inner.AppendAsync(entry, transaction, cancellationToken);
        counter.Increment();
    }
}

internal sealed class Counter
{
    private int _value;

    public int Value => Volatile.Read(ref _value);

    public void Increment() => Interlocked.Increment(ref _value);
}

/// <summary>"The entitlement write fails": throws before the handler's own SaveChanges reaches the database.</summary>
internal sealed class ThrowOnSaveInterceptor : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
        throw new InjectedFaultException("Injected fault in the entitlement write.");
}

/// <summary>"Commit fails": throws on <c>TransactionCommitting</c>, after the real audit append ran and before anything commits.</summary>
internal sealed class ThrowOnCommitInterceptor : DbTransactionInterceptor
{
    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
        throw new InjectedFaultException("Injected fault on commit.");
}

/// <summary>Counts every command the handlers' context sends: zero proves "no database access" on a reachable database.</summary>
internal sealed class CommandCountingInterceptor : DbCommandInterceptor
{
    private int _commands;

    public int Count => Volatile.Read(ref _commands);

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _commands);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Interlocked.Increment(ref _commands);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _commands);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Interlocked.Increment(ref _commands);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _commands);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Interlocked.Increment(ref _commands);
        return base.ScalarExecuting(command, eventData, result);
    }
}

/// <summary>
/// Records every metric tag and every activity name, tag and event tag of the Entitlements and Audit
/// sources, as <c>key=value</c> strings, so a scan can prove a marker never reaches telemetry (G4-24-05).
/// </summary>
internal sealed class TelemetryCapture : IDisposable
{
    private readonly ConcurrentQueue<string> _values = new();
    private readonly MeterListener _meters = new();
    private readonly ActivityListener _activities;

    public TelemetryCapture()
    {
        _meters.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name is EntitlementsModule.TelemetryName or "Decisya.Audit")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _meters.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            _values.Enqueue($"instrument={instrument.Name}");
            foreach (var tag in tags)
            {
                _values.Enqueue($"{tag.Key}={tag.Value}");
            }
        });
        _meters.Start();

        _activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name is EntitlementsModule.TelemetryName or "Decisya.Audit",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                _values.Enqueue($"activity={activity.Source.Name}/{activity.DisplayName}");
                foreach (var tag in activity.TagObjects)
                {
                    _values.Enqueue($"{tag.Key}={tag.Value}");
                }

                foreach (var tag in activity.Events.SelectMany(e => e.Tags))
                {
                    _values.Enqueue($"{tag.Key}={tag.Value}");
                }
            },
        };
        ActivitySource.AddActivityListener(_activities);
    }

    public IReadOnlyCollection<string> Values => _values.ToArray();

    public void Dispose()
    {
        _meters.Dispose();
        _activities.Dispose();
    }
}
