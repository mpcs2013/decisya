using Decisya.SharedKernel.Observability;

namespace Decisya.Modules.Ledger.Contracts;

/// <summary><c>PUT /api/ledger/accounts/{accountId}/name</c> (issue #341, G1 Story 6).</summary>
/// <param name="Name">Required, 1 to 80 characters after trimming (A1). Personal data in the catalogue: never logged.</param>
public sealed record RenameAccountRequest([property: Sensitive] string Name);
