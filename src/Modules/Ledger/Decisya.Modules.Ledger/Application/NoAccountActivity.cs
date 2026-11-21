using System.Collections.Frozen;

namespace Decisya.Modules.Ledger.Application;

/// <summary>
/// No account holds a transaction until EP-04 (G1 assumption A3): an empty answer, and no query.
/// </summary>
internal sealed class NoAccountActivity : IAccountActivity
{
    private static readonly IReadOnlyDictionary<Guid, AccountActivitySummary> Empty =
        FrozenDictionary<Guid, AccountActivitySummary>.Empty;

    public Task<IReadOnlyDictionary<Guid, AccountActivitySummary>> SummarizeAsync(
        IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken) =>
        Task.FromResult(Empty);
}
