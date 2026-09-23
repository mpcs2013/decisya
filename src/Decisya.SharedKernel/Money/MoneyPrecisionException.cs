namespace Decisya.SharedKernel;

/// <summary>
/// Raised when a <see cref="Money"/> is constructed from a decimal amount that carries
/// more fractional digits than its <see cref="Currency"/>'s ISO 4217 minor-unit exponent
/// allows. Per ADR-0006, rounding is never implicit: callers must round explicitly
/// (e.g. <see cref="decimal.Round(decimal, int, MidpointRounding)"/>) before constructing
/// <see cref="Money"/>.
/// </summary>
public sealed class MoneyPrecisionException : ArgumentException
{
    public MoneyPrecisionException(decimal amount, Currency currency)
        // The amount itself is deliberately not in the message (it stays available only
        // via the Amount property): a future ProblemDetails mapper or log sink that
        // returns/echoes Exception.Message must not leak a monetary amount.
        //
        // Currency.ToString() (not .Code) never throws, even for an uninitialised
        // default(Currency); Money's constructor rejects default(Currency), so this
        // exception should never actually see one, but the message stays safe either way
        // (mirrors CurrencyMismatchException).
        : base(
            $"Amount has more fractional digits than {currency} allows " +
            $"(exponent {currency.MinorUnitExponent}). Round explicitly before constructing Money.",
            nameof(amount))
    {
        Amount = amount;
        Currency = currency;
    }

    /// <summary>The offending amount. Not included in <see cref="Exception.Message"/>.</summary>
    public decimal Amount { get; }

    /// <summary>The currency whose precision was exceeded.</summary>
    public Currency Currency { get; }
}
