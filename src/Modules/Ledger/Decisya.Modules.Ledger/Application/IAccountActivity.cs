namespace Decisya.Modules.Ledger.Application;

/// <summary>
/// The one seam between accounts and their transactions (issue #341, G2 "Balance"). Until EP-04 ships
/// <see cref="NoAccountActivity"/> answers it; EP-04 replaces that with a summary query over its own
/// transactions table, in the same module and context, through the tenant filter.
/// </summary>
internal interface IAccountActivity
{
    /// <summary>
    /// The net of each account's transactions and whether it holds one. A missing key means
    /// <c>(0, false)</c>. The caller reads it once per list or read, and once inside the currency-change transaction.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, AccountActivitySummary>> SummarizeAsync(
        IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);
}

/// <summary>An account's transactions in summary: their net in minor units, and whether there is at least one.</summary>
internal readonly record struct AccountActivitySummary(long NetMinor, bool HoldsTransaction);
