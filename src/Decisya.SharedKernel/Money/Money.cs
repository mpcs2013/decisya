using System.Globalization;

namespace Decisya.SharedKernel;

/// <summary>
/// A monetary amount paired with its <see cref="Money.Currency"/>. Amounts are always
/// <see cref="decimal"/> minor units (per CLAUDE.md platform invariant 4 / ADR-0006);
/// <see cref="double"/> is never used for money and is banned repo-wide by
/// <c>BannedSymbols.txt</c>.
///
/// Money never mixes currencies: arithmetic and comparison across currencies throw
/// <see cref="CurrencyMismatchException"/>. Money never rounds implicitly: constructing
/// a Money from a decimal with more fractional digits than the currency's ISO 4217
/// exponent allows throws <see cref="MoneyPrecisionException"/> (round explicitly first).
/// Money does not convert between currencies; that is a separate, out-of-scope concern
/// (see docs/requirements/phase-0/shared-kernel.md).
/// </summary>
public readonly struct Money : IEquatable<Money>, IComparable<Money>
{
    /// <summary>
    /// The largest number of parts or ratios <see cref="Allocate(int)"/> or
    /// <see cref="Allocate(IReadOnlyList{int})"/> accepts. Bounds the memory each call
    /// allocates (several arrays sized to the part count); requesting more throws
    /// <see cref="InvalidAllocationException"/> rather than risking an
    /// <see cref="OutOfMemoryException"/> from an untrusted or malformed caller.
    /// </summary>
    public const int MaxAllocationParts = 10_000;

    public Money(decimal amount, Currency currency)
    {
        if (!currency.IsInitialized)
        {
            throw new ArgumentException(
                "Currency must be created via Currency.FromCode; default(Currency) is not a valid currency.",
                nameof(currency));
        }

        var rounded = decimal.Round(amount, currency.MinorUnitExponent, MidpointRounding.ToEven);
        if (rounded != amount)
        {
            throw new MoneyPrecisionException(amount, currency);
        }

        Amount = amount;
        Currency = currency;
    }

    /// <summary>The amount, in major units (e.g. 100.50 for 100 dollars 50 cents).</summary>
    public decimal Amount { get; }

    /// <summary>The currency this amount is denominated in.</summary>
    public Currency Currency { get; }

    public static Money operator +(Money left, Money right)
    {
        RequireSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        RequireSameCurrency(left, right);
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    public static Money operator -(Money value) => new(-value.Amount, value.Currency);

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    public static bool operator ==(Money left, Money right) => left.Equals(right);

    public static bool operator !=(Money left, Money right) => !left.Equals(right);

    /// <summary>
    /// Splits this amount into <paramref name="parts"/> equal shares that sum back to
    /// exactly this amount, with no lost or gained minor units. For a positive amount,
    /// the leading shares absorb any remainder (e.g. 100.00 USD / 3 =&gt;
    /// 33.34, 33.33, 33.33). A negative amount is the exact mirror image of the same
    /// allocation on its absolute value: <c>(-money).Allocate(n)</c> equals
    /// <c>money.Allocate(n)</c> with every share's sign flipped, share for share and
    /// down to identical midpoint tie-breaks — so a refund's allocation always cancels
    /// the original charge's allocation exactly, party by party.
    /// </summary>
    public IReadOnlyList<Money> Allocate(int parts)
    {
        if (parts <= 0)
        {
            throw new InvalidAllocationException("Allocation part count must be a positive integer.", nameof(parts));
        }

        if (parts > MaxAllocationParts)
        {
            throw new InvalidAllocationException(
                $"Allocation part count must not exceed {MaxAllocationParts}.", nameof(parts));
        }

        var ratios = new int[parts];
        Array.Fill(ratios, 1);
        return Allocate(ratios);
    }

    /// <summary>
    /// Splits this amount by weighted <paramref name="ratios"/> into shares that sum
    /// back to exactly this amount, with no lost or gained minor units. Each share is
    /// proportional to its ratio, within one minor unit. Ties at the exact midpoint
    /// between two minor units are broken deterministically towards an even result
    /// (banker's rounding); remaining ties are broken by ascending ratio index, so
    /// repeating the computation always produces the identical result. A negative
    /// amount allocates the same way as its absolute value and then negates every
    /// share, so <c>Allocate</c> on a negative amount is the exact, share-for-share
    /// mirror image of <c>Allocate</c> on the positive amount for the same ratios
    /// (including which shares absorb the remainder and how midpoint ties resolve).
    /// </summary>
    /// <remarks>
    /// <para>
    /// At most <see cref="MaxAllocationParts"/> ratios are accepted; more throws
    /// <see cref="InvalidAllocationException"/> rather than risking excessive memory use.
    /// </para>
    /// <para>
    /// The amount's minor-unit representation must fit in a signed 64-bit integer
    /// (<see cref="long"/>): roughly ±92.23 quadrillion for a 2-decimal currency (e.g.
    /// USD), ±9.22 quintillion for a 0-decimal currency (e.g. JPY, VND), ±9.22
    /// quadrillion for a 3-decimal currency (e.g. BHD), and ±922.34 trillion for a
    /// 4-decimal currency (e.g. CLF) — see <see cref="Currency.MinorUnitExponent"/>. This
    /// is a narrower range than <see cref="Money"/>'s constructor itself accepts (any
    /// <see cref="decimal"/> the currency's precision allows); an amount outside it
    /// throws <see cref="InvalidAllocationException"/>, not an incidental
    /// <see cref="OverflowException"/>. Intermediate arithmetic uses <see cref="Int128"/>,
    /// so no ratio or part count within the limits above can overflow it.
    /// </para>
    /// </remarks>
    public IReadOnlyList<Money> Allocate(IReadOnlyList<int> ratios)
    {
        ArgumentNullException.ThrowIfNull(ratios);

        // Count is read once and checked (empty, negative, over-limit) before any element
        // is read, so an oversized (or maliciously lazy/expensive) IReadOnlyList<int> is
        // rejected without being scanned (security review 0.04, N2 and N7).
        var count = ratios.Count;
        if (count <= 0)
        {
            throw new InvalidAllocationException(
                "Allocation ratios must be a non-empty list of positive integers.", nameof(ratios));
        }

        if (count > MaxAllocationParts)
        {
            throw new InvalidAllocationException(
                $"Allocation ratio count must not exceed {MaxAllocationParts}.", nameof(ratios));
        }

        // Snapshot the ratios once through the indexer; validation and arithmetic below use
        // only this copy, so a list whose indexer and enumerator disagree (or that changes
        // between reads) cannot slip non-positive ratios past validation (N7).
        var weights = new int[count];
        for (var i = 0; i < count; i++)
        {
            weights[i] = ratios[i];
        }

        if (Array.Exists(weights, static r => r <= 0))
        {
            throw new InvalidAllocationException(
                "Allocation ratios must be a non-empty list of positive integers.", nameof(ratios));
        }

        var exponent = Currency.MinorUnitExponent;

        // Allocate the absolute amount and negate every share at the end, rather than
        // letting negative minor units flow through the floor-division / largest-
        // remainder arithmetic below. This makes Allocate on a negative amount the exact
        // mirror image of Allocate on the positive amount for the same ratios (security
        // review 0.04, N1): a refund's per-party split then always cancels the original
        // charge's per-party split exactly, instead of merely summing to the same total.
        var isNegative = Amount < 0;
        Int128 totalMinorUnits = ToMinorUnits(isNegative ? -Amount : Amount, exponent);

        // Widened to Int128: totalMinorUnits (up to long.MaxValue) times a ratio (up to
        // int.MaxValue) can exceed long.MaxValue (e.g. 50,000,000.00 USD allocated by
        // [int.MaxValue, 1]); Int128 has ample headroom for every value these bounds
        // allow, so this never overflows.
        Int128 totalWeight = 0;
        foreach (var weight in weights)
        {
            totalWeight += weight;
        }

        var floors = new Int128[count];
        var remainders = new Int128[count];
        Int128 floorSum = 0;

        for (var i = 0; i < count; i++)
        {
            var numerator = totalMinorUnits * weights[i];
            var floor = FloorDiv(numerator, totalWeight);
            floors[i] = floor;
            remainders[i] = numerator - (floor * totalWeight);
            floorSum += floor;
        }

        var leftoverUnits = totalMinorUnits - floorSum;

        // Largest-remainder method: the indices with the largest remainder receive the
        // leftover minor units. Exact-midpoint remainders (remainder * 2 == totalWeight)
        // are tie-broken towards making the resulting share even (banker's rounding);
        // any further ties fall back to ascending index, which is what keeps this
        // deterministic and reproducible.
        var order = Enumerable.Range(0, count)
            .OrderByDescending(i => remainders[i])
            .ThenByDescending(i => IsExactMidpoint(remainders[i], totalWeight) && (floors[i] % 2 != 0))
            .ThenBy(i => i)
            .ToArray();

        // Largest-remainder arithmetic guarantees 0 <= leftoverUnits < count; cast
        // is safe, but the loop bound is Int128 either way so a defect here would show up
        // as a genuine failure, never an incidental overflow.
        for (Int128 i = 0; i < leftoverUnits; i++)
        {
            floors[order[(int)i]]++;
        }

        var result = new Money[count];
        for (var i = 0; i < count; i++)
        {
            var minorUnits = (long)floors[i];
            if (isNegative)
            {
                minorUnits = -minorUnits;
            }

            result[i] = new Money(FromMinorUnits(minorUnits, exponent), Currency);
        }

        return result;
    }

    public bool Equals(Money other) => Currency.Equals(other.Currency) && Amount == other.Amount;

    public override bool Equals(object? obj) => obj is Money other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Currency, Amount);

    public int CompareTo(Money other)
    {
        RequireSameCurrency(this, other);
        return Amount.CompareTo(other.Amount);
    }

    /// <summary>
    /// Formats the amount with exactly the currency's number of decimal places, e.g.
    /// "100.00 USD". Safe to call on <c>default(Money)</c> (formats as exactly "0", with
    /// no trailing space and no currency code) even though <c>default(Money)</c> is
    /// never produced by the constructor.
    /// </summary>
    public override string ToString()
    {
        var amountText = Amount.ToString(
            "F" + Currency.MinorUnitExponent.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return Currency.IsInitialized ? $"{amountText} {Currency}" : amountText;
    }

    private static void RequireSameCurrency(Money left, Money right)
    {
        if (left.Currency != right.Currency)
        {
            throw new CurrencyMismatchException(left.Currency, right.Currency);
        }
    }

    private static bool IsExactMidpoint(Int128 remainder, Int128 totalWeight) =>
        totalWeight % 2 == 0 && remainder * 2 == totalWeight;

    /// <summary>Floor division, valid for any dividend and a strictly positive divisor.</summary>
    private static Int128 FloorDiv(Int128 dividend, Int128 divisor)
    {
        var quotient = dividend / divisor;
        var remainder = dividend % divisor;
        return remainder != 0 && remainder < 0 ? quotient - 1 : quotient;
    }

    /// <summary>
    /// Converts a major-unit amount to its minor-unit <see cref="long"/> representation.
    /// </summary>
    /// <exception cref="InvalidAllocationException">
    /// <paramref name="amount"/>'s minor-unit representation does not fit in a
    /// <see cref="long"/> — see the range documented on
    /// <see cref="Allocate(IReadOnlyList{int})"/>. Callers other than <c>Allocate</c>
    /// never hit this: only allocation needs an exact minor-unit integer.
    /// </exception>
    private static long ToMinorUnits(decimal amount, int exponent)
    {
        try
        {
            return decimal.ToInt64(amount * DecimalPow10(exponent));
        }
        catch (OverflowException ex)
        {
            throw new InvalidAllocationException(
                "Amount is too large to allocate: its minor-unit representation does not fit " +
                "a 64-bit integer. See Money.Allocate's XML docs for the exact range.",
                ex);
        }
    }

    private static decimal FromMinorUnits(long minorUnits, int exponent) =>
        minorUnits / DecimalPow10(exponent);

    // Only 0, 2, 3 and 4 appear: no active ISO 4217 currency in IsoCurrencies uses a
    // one-digit minor unit exponent.
    private static decimal DecimalPow10(int exponent) => exponent switch
    {
        0 => 1m,
        2 => 100m,
        3 => 1000m,
        4 => 10000m,
        _ => throw new ArgumentOutOfRangeException(nameof(exponent), exponent, "Unsupported minor-unit exponent."),
    };
}
