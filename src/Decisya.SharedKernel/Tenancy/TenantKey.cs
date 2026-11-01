namespace Decisya.SharedKernel.Tenancy;

/// <summary>
/// The one builder of every Redis and object-storage key that holds a tenant (issue #231, G1
/// Story 5; ADR-0001 "keys are prefixed with the tenant id"; closes register item C-14). A key is
/// built here from an initialized <see cref="TenantId"/> and validated segments, nowhere else: no
/// overload takes a <see cref="string"/>, a <see cref="Guid"/> or an <see cref="object"/> in place
/// of the tenant, so a key can never be built from raw claim text, an empty tenant or text
/// another tenant could shape into a path.
/// </summary>
/// <remarks>
/// <para>
/// The cache key is <c>&lt;tenant&gt;:&lt;s1&gt;:&lt;s2&gt;</c> and the object key is
/// <c>&lt;tenant&gt;/&lt;s1&gt;/&lt;s2&gt;</c>. <c>&lt;tenant&gt;</c> is the 36-character, lowercase,
/// hyphenated GUID text of <see cref="TenantId.Value"/>: a fixed length, and the separators are
/// banned inside segments, so two different tenants can never produce the same key for the same
/// resource name, and a segment can never end one tenant's prefix and start another's.
/// </para>
/// <para>
/// Each segment is 1 to <see cref="MaxSegmentLength"/> characters from <c>[a-z0-9._-]</c>
/// (ordinal ASCII, never <see cref="char.IsLetterOrDigit(char)"/>, so a character is one byte and
/// <c>/</c>, <c>:</c>, whitespace, control characters and non-ASCII text are all refused). The
/// segments <c>.</c> and <c>..</c> are refused, and the whole key is at most
/// <see cref="MaxKeyLength"/> characters. Why those limits: S3 caps an object key at 1024 UTF-8
/// bytes, and 512 leaves half of that for a bucket-side prefix the storage issue may add; Redis
/// allows far longer keys, but short keys save memory; 128 fits a GUID plus a descriptive name.
/// </para>
/// <para>
/// A segment is an identifier or a fixed resource name, never personal data or free text (an
/// e-mail address, a raw transaction description): keys show up in Redis <c>MONITOR</c> and
/// <c>SLOWLOG</c> output and in object-storage access logs (G3 T-17).
/// </para>
/// <para>
/// The builder adds no application prefix, so a key starts with the tenant. A Redis or S3
/// namespace is an option the application sets in its client, outside this type. The Redis
/// session ticket store keys by session and holds no tenant, so it stays outside the builder.
/// </para>
/// </remarks>
public sealed class TenantKey : IEquatable<TenantKey>
{
    /// <summary>The longest single segment, in characters (all ASCII, so also in bytes).</summary>
    public const int MaxSegmentLength = 128;

    /// <summary>The longest whole key, tenant prefix and separators included.</summary>
    public const int MaxKeyLength = 512;

    private const int TenantTextLength = 36;

    private readonly string[] _segments;
    private readonly string _cacheKey;
    private readonly string _objectKey;

    private TenantKey(TenantId tenant, string[] segments)
    {
        Tenant = tenant;
        _segments = segments;

        var tenantText = tenant.Value.ToString("D");
        _cacheKey = tenantText + ':' + string.Join(':', segments);
        _objectKey = tenantText + '/' + string.Join('/', segments);
    }

    /// <summary>The tenant this key belongs to. Always initialized.</summary>
    public TenantId Tenant { get; }

    /// <summary>
    /// Builds a key from an initialized tenant and one or more resource segments.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="tenant"/> is <c>default(<see cref="TenantId"/>)</c>, <paramref name="segments"/>
    /// is empty, a segment breaks the rules above, or the whole key would be longer than
    /// <see cref="MaxKeyLength"/>. The message never carries a segment value.
    /// </exception>
    /// <exception cref="ArgumentNullException">A segment is <see langword="null"/>.</exception>
    public static TenantKey For(TenantId tenant, params ReadOnlySpan<string> segments)
    {
        if (!tenant.IsInitialized)
        {
            throw new ArgumentException(
                "A tenant key needs an initialized TenantId, never default(TenantId).",
                nameof(tenant));
        }

        if (segments.IsEmpty)
        {
            throw new ArgumentException("A tenant key needs at least one resource segment.", nameof(segments));
        }

        var copy = new string[segments.Length];
        var length = TenantTextLength;
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            ArgumentNullException.ThrowIfNull(segment, nameof(segments));
            ValidateSegment(segment);

            length += 1 + segment.Length;
            copy[i] = segment;
        }

        if (length > MaxKeyLength)
        {
            throw new ArgumentException("A tenant key is at most 512 characters long.", nameof(segments));
        }

        return new TenantKey(tenant, copy);
    }

    /// <summary>The Redis key: <c>&lt;tenant&gt;:&lt;s1&gt;:&lt;s2&gt;</c>.</summary>
    public string ToCacheKey() => _cacheKey;

    /// <summary>The object-storage key: <c>&lt;tenant&gt;/&lt;s1&gt;/&lt;s2&gt;</c>.</summary>
    public string ToObjectKey() => _objectKey;

    /// <summary>
    /// The fixed type name, never the key: a key holds a tenant id, and this method is what a
    /// log statement, an exception message or a debugger view reaches by accident. Use
    /// <see cref="ToCacheKey"/> or <see cref="ToObjectKey"/> on purpose.
    /// </summary>
    public override string ToString() => nameof(TenantKey);

    /// <summary>Value equality: the same tenant and the same segments, compared ordinally.</summary>
    public bool Equals(TenantKey? other) =>
        other is not null && string.Equals(_cacheKey, other._cacheKey, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is TenantKey other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_cacheKey);

    private static void ValidateSegment(string segment)
    {
        if (segment.Length is 0 or > MaxSegmentLength)
        {
            throw new ArgumentException("A tenant key segment is 1 to 128 characters long.", nameof(segment));
        }

        if (segment is "." or "..")
        {
            throw new ArgumentException("A tenant key segment is never '.' or '..'.", nameof(segment));
        }

        foreach (var c in segment)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-'))
            {
                throw new ArgumentException(
                    "A tenant key segment holds only the characters a-z, 0-9, '.', '_' and '-'.",
                    nameof(segment));
            }
        }
    }
}
