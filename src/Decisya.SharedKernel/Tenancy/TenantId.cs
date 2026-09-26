using System.Text.Json.Serialization;

namespace Decisya.SharedKernel.Tenancy;

/// <summary>
/// The identifier of a tenant: an organisation or account whose data every persisted
/// aggregate is scoped to (<c>CLAUDE.md</c> invariant 1). Immutable value type; the only
/// way to obtain an initialized instance is <see cref="New"/>, <see cref="From"/>,
/// <see cref="Parse(string?)"/> or the lenient <see cref="TryParse(ReadOnlySpan{char}, out TenantId)"/>
/// — never the struct's implicit parameterless constructor, which produces
/// <c>default(TenantId)</c>, an intentionally uninitialized placeholder that every factory
/// and the JSON converter reject.
/// </summary>
/// <remarks>
/// <see cref="New"/> mints a version-7 GUID (<see cref="Guid.CreateVersion7()"/>) so the
/// value is time-ordered, which keeps a Postgres primary-key index (and any
/// <c>tenant_id</c>-prefixed Redis or object-storage key) from fragmenting the way a random
/// version-4 GUID would. That embedded timestamp exists only for index locality — it is not
/// business time: no member of this type exposes it, and no caller may derive a "created at"
/// instant from a <see cref="TenantId"/> (use <c>IClock</c> instead, per invariant 4).
/// Parsing is lenient about GUID version: any well-formed, non-empty GUID parses, because the
/// exact shape Keycloak's <c>tenant_id</c> claim uses is a later issue's decision and this
/// type must not reject data that predates the version-7 minting convention.
/// </remarks>
[JsonConverter(typeof(TenantIdJsonConverter))]
public readonly struct TenantId : IEquatable<TenantId>
{
    /// <summary>
    /// Input longer than this is rejected before any whitespace trim runs, so rejection
    /// costs O(1) and never depends on the input's size (G4-32-02).
    /// </summary>
    private const int MaxTextLength = 64;

    /// <summary>The exact length of the canonical, hyphenated "D" GUID text form.</summary>
    private const int CanonicalLength = 36;

    private readonly Guid _value;

    private TenantId(Guid value)
    {
        _value = value;
    }

    /// <summary>
    /// The underlying <see cref="Guid"/>. Throws <see cref="InvalidOperationException"/>
    /// when <see cref="IsInitialized"/> is <see langword="false"/> — use this, never
    /// <see cref="ToString"/>, whenever the value is about to become part of a storage key,
    /// a cache key or a query parameter, because <see cref="ToString"/> silently formats an
    /// uninitialized value as an empty string instead of failing.
    /// </summary>
    public Guid Value => IsInitialized
        ? _value
        : throw new InvalidOperationException(
            "This TenantId was never initialised via TenantId.New, TenantId.From or TenantId.Parse; use default(TenantId) only as a placeholder, never as a value.");

    /// <summary>
    /// <see langword="false"/> for <c>default(TenantId)</c>; <see langword="true"/> for any
    /// <see cref="TenantId"/> obtained via <see cref="New"/>, <see cref="From"/>,
    /// <see cref="Parse(string?)"/> or a successful <see cref="TryParse(ReadOnlySpan{char}, out TenantId)"/>.
    /// </summary>
    public bool IsInitialized => _value != Guid.Empty;

    /// <summary>Mints a fresh, initialized tenant identifier as a version-7 GUID.</summary>
    public static TenantId New() => new(Guid.CreateVersion7());

    /// <summary>
    /// Wraps an already-known <see cref="Guid"/> as a <see cref="TenantId"/>.
    /// </summary>
    /// <exception cref="TenantIdFormatException"><paramref name="value"/> is <see cref="Guid.Empty"/>.</exception>
    public static TenantId From(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new TenantIdFormatException();
        }

        return new TenantId(value);
    }

    /// <summary>
    /// Parses the canonical, hyphenated ("D") GUID text form of a tenant identifier — the
    /// same form <see cref="ToString"/> produces. Any GUID version or variant is accepted;
    /// only the text <em>format</em> is narrowed, so two different strings can never name
    /// the same tenant in a cache key, a lock or an audit search.
    /// </summary>
    /// <exception cref="TenantIdFormatException">
    /// <paramref name="value"/> is <see langword="null"/>, not exactly 36 characters after
    /// trimming ASCII whitespace, not in the canonical hyphenated form, or resolves to
    /// <see cref="Guid.Empty"/>.
    /// </exception>
    public static TenantId Parse(string? value)
    {
        if (!TryParseCore(value.AsSpan(), out var guid))
        {
            throw new TenantIdFormatException();
        }

        return new TenantId(guid);
    }

    /// <summary>
    /// Attempts to parse the canonical, hyphenated ("D") GUID text form of a tenant
    /// identifier, without throwing. See <see cref="Parse(string?)"/> for the exact rules.
    /// </summary>
    /// <remarks>
    /// N32-01 (G6 review): there is deliberately no <c>public static bool TryParse(string?,
    /// out TenantId)</c> overload. That exact shape — <c>TryParse(string, out T)</c> or
    /// <c>TryParse(string, IFormatProvider?, out T)</c> — is the model-binding convention
    /// ASP.NET Core minimal APIs and MVC use to bind a route, query-string or header value,
    /// with no <c>IParsable&lt;T&gt;</c> required. A public string-based overload would
    /// make <see cref="TenantId"/> silently route-bindable, which is exactly the cross-tenant
    /// access T-11 describes: the tenant must come only from the validated claim. A caller
    /// with a <see cref="string"/> uses <see cref="Parse(string?)"/> (inside a
    /// <see langword="try"/>, if it must not throw) or this span overload via
    /// <see cref="MemoryExtensions.AsSpan(string?)"/>.
    /// </remarks>
    public static bool TryParse(ReadOnlySpan<char> value, out TenantId tenantId)
    {
        if (TryParseCore(value, out var guid))
        {
            tenantId = new TenantId(guid);
            return true;
        }

        tenantId = default;
        return false;
    }

    /// <summary>
    /// The one parsing path every entry point (<see cref="Parse(string?)"/>,
    /// <see cref="TryParse(ReadOnlySpan{char}, out TenantId)"/> and
    /// <see cref="TenantIdJsonConverter"/>) shares.
    /// </summary>
    /// <remarks>
    /// G3 (G4-32-01): <see cref="Guid.TryParseExact(ReadOnlySpan{char}, ReadOnlySpan{char}, out Guid)"/>
    /// with the "D" format keeps .NET's legacy compatibility parsing, which accepts a
    /// component prefixed with <c>0x</c>, <c>0X</c> or <c>+</c> as long as the component's
    /// length is unchanged — so two textually different strings could parse to the same
    /// GUID. The explicit shape check below (exactly 36 characters; a hyphen at indexes 8,
    /// 13, 18 and 23; an ASCII hex digit everywhere else) runs first and rejects every such
    /// form, independent of runtime parsing internals.
    /// </remarks>
    private static bool TryParseCore(ReadOnlySpan<char> value, out Guid guid)
    {
        guid = Guid.Empty;

        // G4-32-02: the length cap runs before the trim, so an unbounded input is rejected
        // in O(1) without allocating or scanning it.
        if (value.Length > MaxTextLength)
        {
            return false;
        }

        var trimmed = TrimAsciiWhitespace(value);

        if (trimmed.Length != CanonicalLength || !HasCanonicalShape(trimmed))
        {
            return false;
        }

        if (!Guid.TryParseExact(trimmed, "D", out var parsed) || parsed == Guid.Empty)
        {
            return false;
        }

        guid = parsed;
        return true;
    }

    /// <summary>
    /// <see langword="true"/> only for a 36-character span shaped exactly like
    /// <c>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</c>, where every <c>x</c> is an ASCII hex
    /// digit. Rejects the <c>0x</c>/<c>0X</c>/<c>+</c> compatibility prefixes
    /// <see cref="Guid.TryParseExact(ReadOnlySpan{char}, ReadOnlySpan{char}, out Guid)"/>
    /// would otherwise still accept, and any non-ASCII digit, independent of runtime
    /// parsing internals (G3, change 1).
    /// </summary>
    private static bool HasCanonicalShape(ReadOnlySpan<char> value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (i is 8 or 13 or 18 or 23)
            {
                if (value[i] != '-')
                {
                    return false;
                }
            }
            else if (!char.IsAsciiHexDigit(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Trims ASCII whitespace only (never Unicode whitespace, e.g. an em space).</summary>
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

    /// <summary>
    /// Value-based equality on the underlying <see cref="Guid"/>. Does not check
    /// <see cref="IsInitialized"/>: two default, uninitialized instances compare equal, the
    /// same way <c>Guid.Empty == Guid.Empty</c> does. A caller comparing two tenant
    /// identifiers for an ownership or authorization check must assert
    /// <see cref="IsInitialized"/> on both sides first.
    /// </summary>
    public bool Equals(TenantId other) => _value.Equals(other._value);

    public override bool Equals(object? obj) => obj is TenantId other && Equals(other);

    public override int GetHashCode() => _value.GetHashCode();

    /// <summary>
    /// The canonical, lowercase, hyphenated GUID text, or <see cref="string.Empty"/> for
    /// <c>default(TenantId)</c> — never throws. Display and logging only: never build a
    /// storage key, cache key or query value from this. Use <see cref="Value"/>, which
    /// throws when uninitialized, for anything that must fail rather than silently key on
    /// an empty string.
    /// </summary>
    public override string ToString() => IsInitialized ? _value.ToString("D") : string.Empty;

    /// <summary>See <see cref="Equals(TenantId)"/>: does not check <see cref="IsInitialized"/>.</summary>
    public static bool operator ==(TenantId left, TenantId right) => left.Equals(right);

    /// <summary>See <see cref="Equals(TenantId)"/>: does not check <see cref="IsInitialized"/>.</summary>
    public static bool operator !=(TenantId left, TenantId right) => !left.Equals(right);
}
