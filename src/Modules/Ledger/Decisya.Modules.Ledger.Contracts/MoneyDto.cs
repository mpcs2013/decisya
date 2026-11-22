namespace Decisya.Modules.Ledger.Contracts;

/// <summary>
/// Money on the wire (ADR-0027): an amount in the currency's minor units plus its ISO 4217 code. Never a
/// decimal, a string, or a number without its currency. <see cref="AmountMinor"/> lies in
/// <see cref="MinAmountMinor"/> to <see cref="MaxAmountMinor"/>, the JavaScript-safe integer range; the
/// endpoint refuses anything outside it. The second module to put money on the wire moves these constants
/// to one shared place (ADR-0027 point 6).
/// </summary>
/// <param name="AmountMinor">The amount in minor units (JPY 0, EUR 2, KWD 3, CLF 4 decimals).</param>
/// <param name="Currency">Exactly three upper-case ASCII letters, an active ISO 4217 code.</param>
public sealed record MoneyDto(long AmountMinor, string Currency)
{
    /// <summary>The smallest amount the wire accepts or emits: -(2^53 - 1) (ADR-0027 point 2).</summary>
    public const long MinAmountMinor = -9_007_199_254_740_991;

    /// <summary>The largest amount the wire accepts or emits: 2^53 - 1 (ADR-0027 point 2).</summary>
    public const long MaxAmountMinor = 9_007_199_254_740_991;
}
