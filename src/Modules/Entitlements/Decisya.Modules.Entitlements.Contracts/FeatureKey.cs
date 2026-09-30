namespace Decisya.Modules.Entitlements.Contracts;

/// <summary>
/// The name of a gated capability, shaped <c>module.feature</c> (issue #23, G2; ADR-0008):
/// exactly two lower-case segments separated by one dot, each matching
/// <c>[a-z][a-z0-9_]*</c>, at most <see cref="MaxLength"/> characters in total. A well-formed
/// key is not necessarily a known one: <see cref="IEntitlementService.IsEnabledAsync"/>
/// denies every key the plan catalog does not list.
/// </summary>
/// <remarks>
/// The only way to obtain an initialized instance is <see cref="Create"/>,
/// <see cref="TryCreate"/> or a <see cref="FeatureKeys"/> member. <c>default(FeatureKey)</c>
/// is uninitialized: <see cref="Value"/> throws for it, and every evaluation denies it.
/// Comparison is ordinal.
/// </remarks>
public readonly struct FeatureKey : IEquatable<FeatureKey>
{
    /// <summary>The longest key <see cref="Create"/> and <see cref="TryCreate"/> accept.</summary>
    public const int MaxLength = 64;

    private readonly string? _value;

    private FeatureKey(string value)
    {
        _value = value;
    }

    /// <summary><see langword="false"/> for <c>default(FeatureKey)</c> only.</summary>
    public bool IsInitialized => _value is not null;

    /// <summary>The key text. Throws <see cref="InvalidOperationException"/> for <c>default(FeatureKey)</c>.</summary>
    public string Value => _value ?? throw new InvalidOperationException(
        "This FeatureKey was never initialised via FeatureKey.Create, FeatureKey.TryCreate or FeatureKeys.");

    /// <summary>Creates a key from well-formed text.</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not shaped <c>module.feature</c> (see the type summary).</exception>
    public static FeatureKey Create(string value)
    {
        if (!TryCreate(value, out var key))
        {
            throw new ArgumentException(
                "A feature key must be shaped module.feature: two segments matching [a-z][a-z0-9_]*, one dot, at most 64 characters.",
                nameof(value));
        }

        return key;
    }

    /// <summary>Creates a key from text without throwing. <see langword="false"/> for null, empty, too long or malformed text.</summary>
    public static bool TryCreate(string? value, out FeatureKey key)
    {
        if (value is null || !HasValidShape(value))
        {
            key = default;
            return false;
        }

        key = new FeatureKey(value);
        return true;
    }

    /// <summary>A plain ASCII loop, no regular expression: two segments, one dot.</summary>
    private static bool HasValidShape(string value)
    {
        if (value.Length == 0 || value.Length > MaxLength)
        {
            return false;
        }

        var dots = 0;
        var atSegmentStart = true;

        foreach (var c in value)
        {
            if (c == '.')
            {
                if (atSegmentStart || ++dots > 1)
                {
                    return false;
                }

                atSegmentStart = true;
                continue;
            }

            if (atSegmentStart)
            {
                if (c is < 'a' or > 'z')
                {
                    return false;
                }

                atSegmentStart = false;
                continue;
            }

            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            {
                return false;
            }
        }

        return dots == 1 && !atSegmentStart;
    }

    /// <summary>Ordinal equality on the key text; two uninitialized keys are equal.</summary>
    public bool Equals(FeatureKey other) => string.Equals(_value, other._value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is FeatureKey other && Equals(other);

    public override int GetHashCode() => _value is null ? 0 : string.GetHashCode(_value, StringComparison.Ordinal);

    /// <summary>The key text, or <see cref="string.Empty"/> for <c>default(FeatureKey)</c>. Never throws.</summary>
    public override string ToString() => _value ?? string.Empty;

    public static bool operator ==(FeatureKey left, FeatureKey right) => left.Equals(right);

    public static bool operator !=(FeatureKey left, FeatureKey right) => !left.Equals(right);
}
