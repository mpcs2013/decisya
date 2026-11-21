using System.Globalization;

namespace Decisya.Modules.Ledger.Domain;

/// <summary>
/// An account's name (issue #341, G1 assumption A1; G3 M341-05). Trimmed (Unicode whitespace), then 1 to
/// <see cref="MaxLength"/> Unicode scalar values (the same count as Postgres <c>varchar(80)</c>). A lone
/// surrogate or a control character is refused, because Postgres cannot store invalid UTF-16 or <c>\0</c> and
/// would otherwise turn the input into a 500. Otherwise the name is kept exactly as typed: no case change, no
/// normalisation, no encoding. Names need not be unique.
/// </summary>
internal readonly record struct AccountName
{
    /// <summary>The most Unicode scalar values a name holds.</summary>
    internal const int MaxLength = 80;

    private AccountName(string value)
    {
        Value = value;
    }

    /// <summary>The trimmed name. Null only for <c>default(AccountName)</c>, which no factory returns.</summary>
    internal string Value { get; }

    /// <summary>Validates <paramref name="raw"/> and returns the trimmed name.</summary>
    internal static bool TryCreate(string? raw, out AccountName name)
    {
        name = default;
        if (raw is null)
        {
            return false;
        }

        var trimmed = raw.Trim();

        // A name of at most 80 scalar values is at most 160 UTF-16 code units: refuse anything longer
        // before the scan, so the cost never depends on the input's size.
        if (trimmed.Length == 0 || trimmed.Length > MaxLength * 2)
        {
            return false;
        }

        var scalars = 0;
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= trimmed.Length || !char.IsLowSurrogate(trimmed[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return false;
            }
            else if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Control)
            {
                return false;
            }

            scalars++;
            if (scalars > MaxLength)
            {
                return false;
            }
        }

        name = new AccountName(trimmed);
        return true;
    }
}
