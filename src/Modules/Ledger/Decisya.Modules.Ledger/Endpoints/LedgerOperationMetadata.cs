namespace Decisya.Modules.Ledger.Endpoints;

/// <summary>
/// Names the write an endpoint performs (<c>create</c>, <c>rename</c> or <c>currency_change</c>), so the
/// <c>Ledger.Owner</c> handler can log and count a refusal under the right operation (issue #341, G2; A5).
/// </summary>
/// <param name="Operation">One of the operation names in <c>LedgerTelemetry</c>.</param>
internal sealed record LedgerOperationMetadata(string Operation);
