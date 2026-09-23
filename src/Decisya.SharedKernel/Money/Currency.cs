namespace Decisya.SharedKernel;

/// <summary>
/// An ISO 4217 currency: an alphabetic code paired with the minor-unit exponent (number
/// of digits after the decimal point) that code implies. Immutable value type; the only
/// way to obtain one is <see cref="FromCode"/>, which validates the code against the
/// active ISO 4217 table (<see cref="IsoCurrencies"/>).
/// </summary>
public readonly struct Currency : IEquatable<Currency>
{
    private readonly string? _code;

    private Currency(string code, int minorUnitExponent)
    {
        _code = code;
        MinorUnitExponent = minorUnitExponent;
    }

    /// <summary>The upper-case, three-letter ISO 4217 alphabetic code (e.g. "USD").</summary>
    public string Code => _code ?? throw new InvalidOperationException(
        "This Currency was never initialised via Currency.FromCode; use default(Currency) only as a placeholder, never as a value.");

    /// <summary>
    /// The number of digits after the decimal point this currency's minor unit
    /// conventionally uses (e.g. 2 for USD, 0 for JPY, 3 for BHD).
    /// </summary>
    public int MinorUnitExponent { get; }

    /// <summary>
    /// <see langword="false"/> for <c>default(Currency)</c>; <see langword="true"/> for
    /// any <see cref="Currency"/> obtained via <see cref="FromCode"/> or
    /// <see cref="TryFromCode"/>. <c>default(Currency)</c> is never a valid currency —
    /// <see cref="Money"/>'s constructor rejects it — but the struct default is otherwise
    /// unavoidable, so callers that might see one (e.g. <c>default(Money).Currency</c>)
    /// can check this instead of triggering <see cref="Code"/>'s exception.
    /// </summary>
    public bool IsInitialized => _code is not null;

    /// <summary>
    /// Resolves an ISO 4217 alphabetic code to its <see cref="Currency"/>. The code must
    /// be exactly three ASCII letters after trimming leading/trailing ASCII whitespace;
    /// Unicode look-alike letters (e.g. the Latin small letter long S, which
    /// case-folds to "S") and non-ASCII whitespace (e.g. an em space) are rejected
    /// rather than silently normalised, so this always agrees with an upstream
    /// <c>^[A-Z]{3}$</c> validator.
    /// </summary>
    /// <exception cref="InvalidCurrencyCodeException">
    /// The code is null, empty, malformed, or not an active ISO 4217 currency code.
    /// </exception>
    public static Currency FromCode(string code)
    {
        if (!TryNormalize(code, out var normalized) || !IsoCurrencies.TryGetExponent(normalized, out var exponent))
        {
            throw new InvalidCurrencyCodeException(code);
        }

        return new Currency(normalized, exponent);
    }

    /// <summary>
    /// Attempts to resolve an ISO 4217 alphabetic code, without throwing. See
    /// <see cref="FromCode"/> for the exact validation rules.
    /// </summary>
    public static bool TryFromCode(string? code, out Currency currency)
    {
        if (code is not null && TryNormalize(code, out var normalized) &&
            IsoCurrencies.TryGetExponent(normalized, out var exponent))
        {
            currency = new Currency(normalized, exponent);
            return true;
        }

        currency = default;
        return false;
    }

    /// <summary>
    /// Trims ASCII whitespace only (never Unicode whitespace, e.g. an em space) and
    /// requires exactly three ASCII letters (never a Unicode look-alike), upper-cased.
    /// </summary>
    private static bool TryNormalize(string? code, out string normalized)
    {
        normalized = string.Empty;

        if (code is null)
        {
            return false;
        }

        var trimmed = TrimAsciiWhitespace(code.AsSpan());
        if (trimmed.Length != 3)
        {
            return false;
        }

        Span<char> upper = stackalloc char[3];
        for (var i = 0; i < 3; i++)
        {
            if (!char.IsAsciiLetter(trimmed[i]))
            {
                return false;
            }

            upper[i] = char.ToUpperInvariant(trimmed[i]);
        }

        normalized = new string(upper);
        return true;
    }

    private static ReadOnlySpan<char> TrimAsciiWhitespace(ReadOnlySpan<char> value)
    {
        var start = 0;
        while (start < value.Length && IsAsciiWhitespace(value[start]))
        {
            start++;
        }

        var end = value.Length;
        while (end > start && IsAsciiWhitespace(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }

    private static bool IsAsciiWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f' or '\v';

    public bool Equals(Currency other) =>
        string.Equals(_code, other._code, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Currency other && Equals(other);

    public override int GetHashCode() =>
        _code is null ? 0 : string.GetHashCode(_code, StringComparison.Ordinal);

    public override string ToString() => _code ?? string.Empty;

    public static bool operator ==(Currency left, Currency right) => left.Equals(right);

    public static bool operator !=(Currency left, Currency right) => !left.Equals(right);
}
