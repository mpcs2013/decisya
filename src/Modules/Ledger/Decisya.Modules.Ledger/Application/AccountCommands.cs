using Decisya.Modules.Ledger.Contracts;
using Decisya.Modules.Ledger.Domain;
using Decisya.SharedKernel;
using NodaTime;

namespace Decisya.Modules.Ledger.Application;

/// <summary>The validated input of a create (the endpoint has already refused every invalid shape).</summary>
internal sealed record CreateAccount(AccountName Name, Currency Currency, Money? OpeningBalance, LocalDate? OpeningDate);

/// <summary>The validated input of a rename.</summary>
internal sealed record RenameAccount(Guid AccountId, AccountName Name);

/// <summary>The validated input of a currency change.</summary>
internal sealed record ChangeAccountCurrency(Guid AccountId, Currency Currency, Money? OpeningBalance, LocalDate? OpeningDate);

/// <summary>How a write ended. The endpoint maps each value to one HTTP status.</summary>
internal enum LedgerOutcome
{
    Succeeded = 1,
    Forbidden = 2,
    NotFound = 3,
    LimitReached = 4,
    CurrencyLocked = 5,
    CurrencyUnchanged = 6,
}

/// <summary>A write's outcome and, on success, the account as the API returns it.</summary>
internal readonly record struct AccountCommandResult(LedgerOutcome Outcome, AccountDto? Account);
