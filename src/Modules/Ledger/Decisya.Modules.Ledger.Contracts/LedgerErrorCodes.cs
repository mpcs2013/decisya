namespace Decisya.Modules.Ledger.Contracts;

/// <summary>
/// The <c>code</c> member of a <c>/api/ledger</c> problem body (issue #341, G2). A 400 and the two 409s carry
/// one; a 403 and a 404 never do. Stable: a rename is a breaking change for the SPA client.
/// </summary>
public static class LedgerErrorCodes
{
    /// <summary>The body is not valid JSON of the expected shape, is too large, or has the wrong content type.</summary>
    public const string BodyInvalid = "ledger.body_invalid";

    /// <summary>The account name is not 1 to 80 characters after trimming, or holds a control character or a lone surrogate.</summary>
    public const string NameInvalid = "ledger.name_invalid";

    /// <summary>A currency is not three upper-case ASCII letters, or not an active ISO 4217 code.</summary>
    public const string CurrencyInvalid = "ledger.currency_invalid";

    /// <summary>An amount lies outside the wire range of ADR-0027.</summary>
    public const string AmountInvalid = "ledger.amount_invalid";

    /// <summary>The opening balance's currency differs from the account's.</summary>
    public const string CurrencyMismatch = "ledger.currency_mismatch";

    /// <summary>The opening date is not a calendar date <c>yyyy-MM-dd</c> in years 1 to 9999.</summary>
    public const string OpeningDateInvalid = "ledger.opening_date_invalid";

    /// <summary>The opening balance and the opening date were not given together.</summary>
    public const string OpeningPairInvalid = "ledger.opening_pair_invalid";

    /// <summary>The new currency equals the account's current one.</summary>
    public const string CurrencyUnchanged = "ledger.currency_unchanged";

    /// <summary>The space already holds the most accounts it may hold.</summary>
    public const string AccountLimitReached = "ledger.account_limit_reached";

    /// <summary>The account holds a transaction, so its currency can no longer change.</summary>
    public const string CurrencyLocked = "ledger.currency_locked";
}
