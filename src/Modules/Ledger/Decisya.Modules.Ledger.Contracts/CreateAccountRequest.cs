using Decisya.SharedKernel.Observability;

namespace Decisya.Modules.Ledger.Contracts;

/// <summary>
/// <c>POST /api/ledger/accounts</c> (issue #341, G1 Story 1). No tenant, id, balance or lock member can be
/// expressed: an unknown member is refused. The opening balance and its date come together or not at all (A2).
/// </summary>
/// <param name="Name">Required, 1 to 80 characters after trimming (A1). Personal data in the catalogue: never logged.</param>
/// <param name="Currency">Exactly three upper-case ASCII letters, an active ISO 4217 code.</param>
/// <param name="OpeningBalance">Optional; absent means zero. Must carry the account's own currency.</param>
/// <param name="OpeningDate">Optional ISO calendar date (<c>yyyy-MM-dd</c>); present exactly when the balance is.</param>
public sealed record CreateAccountRequest(
    [property: Sensitive] string Name,
    string Currency,
    MoneyDto? OpeningBalance = null,
    string? OpeningDate = null);
