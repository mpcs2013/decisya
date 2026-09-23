namespace Decisya.SharedKernel;

/// <summary>
/// Static table of active ISO 4217 alphabetic currency codes and their minor-unit
/// exponents (the number of digits after the decimal point conventionally used for the
/// currency's minor unit, e.g. USD =&gt; 2 cents, JPY =&gt; 0, BHD =&gt; 3 fils).
/// </summary>
/// <remarks>
/// Source: ISO 4217 "Codes for the representation of currencies" as maintained by the
/// ISO 4217 Maintenance Agency (SIX Interbank Clearing Ltd, on behalf of ISO), the
/// published list of active currency &amp; fund codes, amendment history current through
/// amendment 176 (effective 2024-08-29). Transcribed from public ISO 4217 reference
/// tables as of the ISO 4217 Maintenance Agency's 2024-08 edition
/// (https://www.six-group.com/en/products-services/financial-information/data-standards.html,
/// "List One - Currency, fund and precious metal codes"). This is a point-in-time,
/// best-effort transcription rather than a live feed; review against the current
/// published list before relying on it for a newly issued or redenominated currency.
///
/// Scope: covers national/territorial circulating currencies and the small set of
/// "funds code" settlement currencies with a conventional decimal minor unit (BOV, CHE,
/// CHW, COU, MXV, USN). Precious-metal codes (XAU, XAG, XPD, XPT), the IMF Special
/// Drawing Right (XDR), bond-market units (XBA-XBD), the Sucre (XSU), the ADB Unit of
/// Account (XUA), the test code XTS and "no currency" code XXX are intentionally
/// excluded: they have no conventional decimal minor unit and are not currencies a
/// tenant's ledger, budget or forecast would denominate an amount in. Add them as a
/// deliberate, reviewed change if a future module needs them (e.g. a commodities
/// module), not silently here.
/// </remarks>
internal static class IsoCurrencies
{
    /// <summary>Currencies whose minor unit has zero decimal digits (e.g. JPY).</summary>
    private static readonly string[] ZeroDecimalCodes =
    [
        "BIF", "CLP", "DJF", "GNF", "ISK", "JPY", "KMF", "KRW", "PYG", "RWF",
        "UGX", "UYI", "VND", "VUV", "XAF", "XOF", "XPF",
    ];

    /// <summary>Currencies whose minor unit has three decimal digits (e.g. BHD).</summary>
    private static readonly string[] ThreeDecimalCodes =
    [
        "BHD", "IQD", "JOD", "KWD", "LYD", "OMR", "TND",
    ];

    /// <summary>Currencies whose minor unit has four decimal digits.</summary>
    private static readonly string[] FourDecimalCodes =
    [
        "CLF", "UYW",
    ];

    /// <summary>
    /// All other active ISO 4217 currency and funds codes; these use the conventional
    /// two decimal digits.
    /// </summary>
    private static readonly string[] TwoDecimalCodes =
    [
        "AED", "AFN", "ALL", "AMD", "ANG", "AOA", "ARS", "AUD", "AWG", "AZN",
        "BAM", "BBD", "BDT", "BGN", "BMD", "BND", "BOB", "BOV", "BRL", "BSD",
        "BTN", "BWP", "BYN", "BZD",
        "CAD", "CDF", "CHE", "CHF", "CHW", "CNY", "COP", "COU", "CRC", "CUP",
        "CVE", "CZK",
        "DKK", "DOP", "DZD",
        "EGP", "ERN", "ETB", "EUR",
        "FJD", "FKP",
        "GBP", "GEL", "GHS", "GIP", "GMD", "GTQ", "GYD",
        "HKD", "HNL", "HTG", "HUF",
        "IDR", "ILS", "INR", "IRR",
        "JMD",
        "KES", "KGS", "KHR", "KPW", "KYD", "KZT",
        "LAK", "LBP", "LKR", "LRD", "LSL",
        "MAD", "MDL", "MGA", "MKD", "MMK", "MNT", "MOP", "MRU", "MUR", "MVR",
        "MWK", "MXN", "MXV", "MYR", "MZN",
        "NAD", "NGN", "NIO", "NOK", "NPR", "NZD",
        "PAB", "PEN", "PGK", "PHP", "PKR", "PLN",
        "QAR",
        "RON", "RSD", "RUB",
        "SAR", "SBD", "SCR", "SDG", "SEK", "SGD", "SHP", "SLE", "SOS", "SRD",
        "SSP", "STN", "SVC", "SYP", "SZL",
        "THB", "TJS", "TMT", "TOP", "TRY", "TTD", "TWD", "TZS",
        "UAH", "USD", "USN", "UYU", "UZS",
        "VES",
        "WST",
        "XCD",
        "YER",
        "ZAR", "ZMW", "ZWG",
    ];

    private static readonly Dictionary<string, int> Exponents = BuildTable();

    public static bool TryGetExponent(string code, out int exponent) =>
        Exponents.TryGetValue(code, out exponent);

    private static Dictionary<string, int> BuildTable()
    {
        var table = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var code in ZeroDecimalCodes)
        {
            table.Add(code, 0);
        }

        foreach (var code in TwoDecimalCodes)
        {
            table.Add(code, 2);
        }

        foreach (var code in ThreeDecimalCodes)
        {
            table.Add(code, 3);
        }

        foreach (var code in FourDecimalCodes)
        {
            table.Add(code, 4);
        }

        return table;
    }
}
