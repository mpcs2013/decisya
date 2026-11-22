namespace Decisya.Modules.Ledger.Contracts;

/// <summary>
/// <c>PUT /api/ledger/accounts/{accountId}/currency</c> (issue #341, G1 Story 5). No member has a default:
/// every member must be present (null is allowed for the two opening members), so a currency change always
/// restates the opening balance.
/// </summary>
/// <param name="Currency">The new currency: three upper-case ASCII letters, an active ISO 4217 code, different from the account's.</param>
/// <param name="OpeningBalance">The restated opening balance in the new currency, or null for none.</param>
/// <param name="OpeningDate">The restated opening date, present exactly when the balance is.</param>
public sealed record ChangeAccountCurrencyRequest(string Currency, MoneyDto? OpeningBalance, string? OpeningDate);
