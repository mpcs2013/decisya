using System.Data.Common;
using Decisya.Modules.Audit.Contracts;
using Decisya.Modules.Ledger.Application;
using Decisya.Modules.Ledger.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Decisya.Modules.Ledger.Tests.TestSupport;

/// <summary>A deliberately injected failure (G1 Story 7 fault scenarios). Its message never carries command data.</summary>
internal sealed class InjectedFaultException(string message) : Exception(message);

/// <summary>
/// Test seams for the NFR-83 fault matrix. They hang off <see cref="LedgerHarness"/>'s <c>configure</c> hook, so
/// the handlers run exactly as the API wires them, in a real DI scope, as the real <c>decisya_ledger</c> role.
/// </summary>
internal static class LedgerFaults
{
    /// <summary>Wraps the real <see cref="IAuditWriter"/> (the Audit module's internal type, created by the container) in a decorator.</summary>
    public static Action<IServiceCollection> Decorate(Func<IAuditWriter, IAuditWriter> decorator) => services =>
    {
        var original = services.Single(d => d.ServiceType == typeof(IAuditWriter));
        var implementation = original.ImplementationType
            ?? throw new InvalidOperationException("The audit writer registration has no implementation type.");
        services.Remove(original);
        services.AddScoped<IAuditWriter>(sp => decorator((IAuditWriter)ActivatorUtilities.CreateInstance(sp, implementation)));
    };

    /// <summary>Adds interceptors to the handlers' own <c>LedgerDbContext</c> options (a second <c>AddDbContext</c> call merges).</summary>
    public static Action<IServiceCollection> Intercept(params IInterceptor[] interceptors) => services =>
        services.AddDbContext<LedgerDbContext>(options => options.AddInterceptors(interceptors));

    /// <summary>Replaces the activity seam: every account then holds a transaction (the currency is locked, A3).</summary>
    public static Action<IServiceCollection> AccountsHoldATransaction() => services =>
        services.AddScoped<IAccountActivity, HoldingActivity>();

    public static Action<IServiceCollection> All(params Action<IServiceCollection>[] configurations) => services =>
    {
        foreach (var configuration in configurations)
        {
            configuration(services);
        }
    };
}

/// <summary>The EP-04 stand-in for G1 Story 5's "holds one transaction": every given account holds one.</summary>
internal sealed class HoldingActivity : IAccountActivity
{
    public Task<IReadOnlyDictionary<Guid, AccountActivitySummary>> SummarizeAsync(
        IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, AccountActivitySummary> summaries =
            accountIds.ToDictionary(id => id, _ => new AccountActivitySummary(0, true));
        return Task.FromResult(summaries);
    }
}

/// <summary>Runs the real append, then throws: "something fails after the audit append succeeded".</summary>
internal sealed class ThrowAfterAppendWriter(IAuditWriter inner) : IAuditWriter
{
    public async Task AppendAsync(AuditEntry entry, DbTransaction transaction, CancellationToken cancellationToken = default)
    {
        await inner.AppendAsync(entry, transaction, cancellationToken);
        throw new InjectedFaultException("Injected fault after a real audit append.");
    }
}

/// <summary>"The account write fails": throws before the handler's own SaveChanges reaches the database, while armed.</summary>
internal sealed class ThrowOnSaveInterceptor : SaveChangesInterceptor
{
    private volatile bool _armed;

    public ThrowOnSaveInterceptor(bool armed = true)
    {
        _armed = armed;
    }

    public bool Armed
    {
        get => _armed;
        set => _armed = value;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
        _armed
            ? throw new InjectedFaultException("Injected fault in the account write.")
            : base.SavingChangesAsync(eventData, result, cancellationToken);
}

/// <summary>"Commit fails": throws on <c>TransactionCommitting</c>, after the real audit append ran and before anything commits, while armed.</summary>
internal sealed class ThrowOnCommitInterceptor : DbTransactionInterceptor
{
    private volatile bool _armed;

    public ThrowOnCommitInterceptor(bool armed = true)
    {
        _armed = armed;
    }

    public bool Armed
    {
        get => _armed;
        set => _armed = value;
    }

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
        _armed
            ? throw new InjectedFaultException("Injected fault on commit.")
            : base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
}

/// <summary>Counts every command the handlers' context sends: zero proves "no query reaches the ledger tables" on a reachable database.</summary>
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
