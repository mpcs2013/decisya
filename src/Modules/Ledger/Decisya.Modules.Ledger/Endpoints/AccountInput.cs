using Decisya.Modules.Ledger.Contracts;
using Decisya.SharedKernel;
using NodaTime;
using NodaTime.Text;

namespace Decisya.Modules.Ledger.Endpoints;

/// <summary>
/// The shape checks that run after parsing and before any database access (issue #341, G2 "Validation"; G3
/// M341-05 (c)). Each returns a <see cref="LedgerErrorCodes"/> value, or <see langword="null"/> when the input is
/// valid. Nothing here logs or echoes a value.
/// </summary>
internal static class AccountInput
{
    /// <summary>
    /// A currency on the wire is exactly three upper-case ASCII letters (ordinal) and an active ISO 4217 code.
    /// The ordinal check comes <b>before</b> <see cref="Currency.TryFromCode"/>, which trims and upper-cases:
    /// <c>"eur"</c> and <c>" EUR"</c> are refused (ADR-0027 point 4).
    /// </summary>
    public static bool TryReadCurrency(string? code, out Currency currency)
    {
        currency = default;
        if (code is null || code.Length != 3)
        {
            return false;
        }

        foreach (var c in code)
        {
            if (c is < 'A' or > 'Z')
            {
                return false;
            }
        }

        return Currency.TryFromCode(code, out currency);
    }

    /// <summary>
    /// The opening balance and date of a create or currency change, in the order of the G2 table: the balance's
    /// own currency, the currency match, the amount range, the date, then the pairing (A2).
    /// </summary>
    public static string? ReadOpening(
        Currency accountCurrency,
        MoneyDto? openingBalance,
        string? openingDate,
        out Money? balance,
        out LocalDate? date)
    {
        balance = null;
        date = null;

        Currency balanceCurrency = default;
        if (openingBalance is not null && !TryReadCurrency(openingBalance.Currency, out balanceCurrency))
        {
            return LedgerErrorCodes.CurrencyInvalid;
        }

        if (openingBalance is not null && balanceCurrency != accountCurrency)
        {
            return LedgerErrorCodes.CurrencyMismatch;
        }

        if (openingBalance is not null
            && (openingBalance.AmountMinor < MoneyDto.MinAmountMinor || openingBalance.AmountMinor > MoneyDto.MaxAmountMinor))
        {
            return LedgerErrorCodes.AmountInvalid;
        }

        if (openingDate is not null)
        {
            if (!TryReadDate(openingDate, out var parsed))
            {
                return LedgerErrorCodes.OpeningDateInvalid;
            }

            date = parsed;
        }

        if ((openingBalance is null) != (openingDate is null))
        {
            return LedgerErrorCodes.OpeningPairInvalid;
        }

        if (openingBalance is not null)
        {
            balance = Money.FromMinorUnits(openingBalance.AmountMinor, accountCurrency);
        }

        return null;
    }

    /// <summary>A strict ISO calendar date <c>yyyy-MM-dd</c> with a year from 1 to 9999 (the <see cref="DateOnly"/> range).</summary>
    private static bool TryReadDate(string text, out LocalDate date)
    {
        date = default;
        if (text.Length != 10)
        {
            return false;
        }

        var parsed = LocalDatePattern.Iso.Parse(text);
        if (!parsed.Success || parsed.Value.Year is < 1 or > 9999)
        {
            return false;
        }

        date = parsed.Value;
        return true;
    }
}
