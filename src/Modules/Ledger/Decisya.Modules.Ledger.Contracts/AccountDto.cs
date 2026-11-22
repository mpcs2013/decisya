using Decisya.SharedKernel.Observability;

namespace Decisya.Modules.Ledger.Contracts;

/// <summary>
/// One account as the API returns it (issue #341, G1 Story 2). Carries no tenant id, actor, role or permission
/// member (F341-04).
/// </summary>
/// <param name="Id">The account's opaque id.</param>
/// <param name="Name">The name exactly as stored (A1). Personal data in the catalogue: never logged.</param>
/// <param name="Currency">The account's currency.</param>
/// <param name="OpeningBalance">The opening balance (zero when none was given).</param>
/// <param name="OpeningDate">The ISO calendar date of the opening balance, or null.</param>
/// <param name="Balance">The opening balance plus the account's transactions (EP-04); equals the opening balance until then.</param>
/// <param name="CurrencyLocked">True once the account holds a transaction (A3); always false until EP-04.</param>
public sealed record AccountDto(
    Guid Id,
    [property: Sensitive] string Name,
    string Currency,
    MoneyDto OpeningBalance,
    string? OpeningDate,
    MoneyDto Balance,
    bool CurrencyLocked);
