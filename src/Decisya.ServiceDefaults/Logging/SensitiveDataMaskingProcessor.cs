using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Decisya.SharedKernel.Observability;

namespace Decisya.ServiceDefaults.Logging;

/// <summary>
/// The single masking core. Both sinks — the <c>decisya-json</c> stdout formatter and the
/// OTLP <see cref="MaskingLogRecordProcessor"/> — run every log record through this class,
/// so what reaches the console and what reaches the collector can never drift apart.
/// </summary>
/// <remarks>
/// <para>Rules, applied per key/value pair:</para>
/// <list type="number">
/// <item><description>Key deny-list (defence in depth) → <see cref="Mask"/>.</description></item>
/// <item><description>Scalars pass through; a <see cref="Uri"/> loses its userinfo, query and fragment.</description></item>
/// <item><description><see cref="SensitiveAttribute"/> on the value's runtime type → <see cref="Mask"/>.</description></item>
/// <item><description>Types whose graph contains a <see cref="SensitiveAttribute"/>, or cannot be determined,
/// are rendered by this class and never by <c>ToString()</c> — and only when the type is
/// Decisya-declared, anonymous or tuple-like. Everything else is masked whole.</description></item>
/// <item><description>Collections whose element type is not scalar, or is unknown, are masked whole.</description></item>
/// <item><description>Other non-scalar types with a fully known, non-sensitive graph pass through.</description></item>
/// <item><description>Fail closed: any failure masks the value, never rethrows, never logs, and always
/// leaves the rest of the record intact.</description></item>
/// </list>
/// </remarks>
public sealed partial class SensitiveDataMaskingProcessor
{
    /// <summary>The placeholder every masked value is replaced by.</summary>
    public const string Mask = "***";

    /// <summary>Attribute added to a record whose masking hit an error or an unstructured state.</summary>
    public const string MaskingErrorKey = "decisya.masking_error";

    internal const string OriginalFormatKey = "{OriginalFormat}";
    internal const string UnstructuredStateError = "unstructured_state";
    internal const string TruncationMarker = "…[truncated]";

    /// <summary>
    /// L-4 (G6 review): how much further than <see cref="MaxStringLength"/> (or another
    /// caller's <c>limit</c>) <see cref="MaskString"/> keeps before regex-matching, so the
    /// shape regexes never scan an unbounded attacker-controlled string. A match spanning
    /// the margin is still found; content beyond it is truncated either way, so nothing
    /// user-visible is lost by bounding the scan.
    /// </summary>
    internal const int MaskStringMatchMargin = 4_096;

    internal const int MaxStringLength = 8_192;
    internal const int MaxExceptionTextLength = 32_768;
    internal const int MaxMembersPerObject = 32;
    internal const int MaxRenderedNodesPerRecord = 256;
    internal const int MaxRenderDepth = 3;

    /// <summary>
    /// Key fragments that mask their value outright. This is a backstop, not the control:
    /// <see cref="SensitiveAttribute"/> is the control. Comparison is case-insensitive
    /// after <c>_</c>, <c>-</c> and <c>.</c> are removed, and matches a substring, so
    /// <c>Api-Key</c>, <c>access_token</c> and <c>UserIdHashKey</c> all hit.
    /// </summary>
    private static readonly string[] DeniedKeyFragments =
    [
        "password",
        "passwd",
        "pwd",
        "secret",
        "token",
        "apikey",
        "authorization",
        "cookie",
        "connectionstring",
        "querystring",
        "credential",
        "privatekey",
        "signingkey",
        "hashkey",
        "bearer",
        "jwt",
        "session",
    ];

    /// <summary>
    /// Masks one log record's state. The <c>{OriginalFormat}</c> pair is lifted out into
    /// <see cref="MaskedState.Template"/> rather than kept as an attribute.
    /// </summary>
    public MaskedState Process(IReadOnlyList<KeyValuePair<string, object?>>? state)
    {
        var attributes = new List<KeyValuePair<string, object?>>((state?.Count ?? 0) + 1);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? template = null;
        var anyMasked = false;
        var valueError = false;

        if (state is not null)
        {
            var budget = new RenderBudget();

            for (var i = 0; i < state.Count; i++)
            {
                var pair = state[i];
                var key = pair.Key ?? string.Empty;

                if (string.Equals(key, OriginalFormatKey, StringComparison.Ordinal))
                {
                    template ??= pair.Value as string;
                    continue;
                }

                if (!seen.Add(key))
                {
                    continue;
                }

                MaskResult result;
                try
                {
                    result = ProcessKeyed(key, pair.Value, depth: 0, budget);
                }
                catch (Exception)
                {
                    ObservabilityDiagnostics.RecordMaskingError("state");
                    result = new MaskResult(Mask, Masked: true);
                    valueError = true;
                }

                anyMasked |= result.Masked;
                attributes.Add(new KeyValuePair<string, object?>(key, result.Value));
            }
        }

        var unstructured = template is null;
        if (unstructured)
        {
            // Rule 7: nothing tells us which parts of the state the message was built from,
            // so the message itself is masked and the record is flagged.
            anyMasked = true;
            ObservabilityDiagnostics.RecordMaskingError(UnstructuredStateError);
        }

        if ((unstructured || valueError) && seen.Add(MaskingErrorKey))
        {
            attributes.Add(new KeyValuePair<string, object?>(
                MaskingErrorKey,
                unstructured ? UnstructuredStateError : true));
        }

        return new MaskedState(attributes, anyMasked, template);
    }

    /// <summary>
    /// Masks a single value held under <paramref name="key"/>. Used for logging scopes,
    /// which are not part of a record's state.
    /// </summary>
    public object? ProcessValue(string key, object? value, out bool masked)
    {
        try
        {
            var result = ProcessKeyed(key, value, depth: 0, new RenderBudget());
            masked = result.Masked;
            return result.Value;
        }
        catch (Exception)
        {
            ObservabilityDiagnostics.RecordMaskingError("value");
            masked = true;
            return Mask;
        }
    }

    /// <summary>
    /// The single exception seam both sinks use. Token-shaped text is masked in the
    /// message and in the full <see cref="Exception.ToString()"/> text, inner exceptions
    /// included. The exception <em>type</em> and the stack trace survive, because an
    /// incident needs them.
    /// </summary>
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The exception seam is part of the masking core's instance surface. Both sinks hold a " +
                        "SensitiveDataMaskingProcessor and call it alongside Process and ProcessValue; making it " +
                        "static would split the seam across two call styles and block per-instance configuration.")]
    public (string Type, string Message, string StackTrace) ProcessException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        try
        {
            var runtimeType = exception.GetType();
            return (
                runtimeType.FullName ?? runtimeType.Name,
                MaskString(exception.Message ?? string.Empty, MaxStringLength, out _),
                MaskString(exception.ToString(), MaxExceptionTextLength, out _));
        }
        catch (Exception)
        {
            ObservabilityDiagnostics.RecordMaskingError("exception");
            return (Mask, Mask, Mask);
        }
    }

    /// <summary>
    /// <see langword="true"/> when <paramref name="key"/> contains a denied fragment, after
    /// case folding and removing <c>_</c>, <c>-</c> and <c>.</c>.
    /// </summary>
    internal static bool IsDeniedKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        Span<char> buffer = key.Length <= 256 ? stackalloc char[256] : new char[key.Length];
        var length = 0;
        foreach (var character in key)
        {
            if (character is '_' or '-' or '.')
            {
                continue;
            }

            buffer[length++] = char.ToLowerInvariant(character);
        }

        ReadOnlySpan<char> normalized = buffer[..length];
        foreach (var fragment in DeniedKeyFragments)
        {
            if (normalized.Contains(fragment, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Value-shape masking: replaces JWT-shaped and <c>Bearer &lt;token&gt;</c>-shaped
    /// substrings with <see cref="Mask"/>, then truncates at <paramref name="limit"/>.
    /// A token logged as a plain string under an innocent key name is the residual risk
    /// the key deny-list cannot cover; these two shapes are the most common instances.
    /// </summary>
    /// <remarks>
    /// L-4 (G6 review): truncates to <paramref name="limit"/> plus
    /// <see cref="MaskStringMatchMargin"/> <em>before</em> running the shape regexes, then
    /// truncates again to the exact limit afterwards. Matching first and truncating
    /// afterwards let the regex engine rescan an unbounded, attacker-controlled string from
    /// every <c>eyJ</c> start (quadratic); the per-call 1 s regex timeout bounds it, and a
    /// timeout still fails closed to <see cref="Mask"/> (<see cref="ProcessKeyed"/>'s
    /// caller), but one log call could still cost seconds of CPU. Content beyond the margin
    /// is truncated either way, so bounding the scan loses nothing user-visible. The
    /// redundant <c>IsMatch</c> before each <c>Replace</c> is also dropped:
    /// <see cref="Regex.Replace(string, string)"/> already returns the original string
    /// reference, with no allocation, when there is no match.
    /// </remarks>
    internal static string MaskString(string value, int limit, out bool changed)
    {
        var truncatedForMatching = false;
        var result = value;

        var matchWindow = limit + MaskStringMatchMargin;
        if (result.Length > matchWindow)
        {
            result = result[..matchWindow];
            truncatedForMatching = true;
        }

        result = JwtShape().Replace(result, Mask);
        result = BearerShape().Replace(result, Mask);

        if (result.Length > limit)
        {
            result = string.Concat(result.AsSpan(0, limit), TruncationMarker);
        }

        changed = truncatedForMatching || !ReferenceEquals(result, value);
        return result;
    }

    /// <summary>
    /// Renders a <see cref="Uri"/> as scheme, host, port and path only: the userinfo is
    /// dropped, a present query becomes <c>?***</c> and the fragment is dropped. Both can
    /// carry a token or an identifier.
    /// </summary>
    internal static string RenderUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
        {
            var original = uri.OriginalString;

            var fragmentIndex = original.IndexOf('#', StringComparison.Ordinal);
            if (fragmentIndex >= 0)
            {
                original = original[..fragmentIndex];
            }

            var queryIndex = original.IndexOf('?', StringComparison.Ordinal);
            return queryIndex >= 0 ? string.Concat(original.AsSpan(0, queryIndex), "?", Mask) : original;
        }

        var builder = new StringBuilder(uri.Scheme).Append("://").Append(uri.Host);

        if (!uri.IsDefaultPort)
        {
            builder.Append(':').Append(uri.Port.ToString(CultureInfo.InvariantCulture));
        }

        builder.Append(uri.AbsolutePath);

        if (!string.IsNullOrEmpty(uri.Query))
        {
            builder.Append('?').Append(Mask);
        }

        return builder.ToString();
    }

    private MaskResult ProcessKeyed(string key, object? value, int depth, RenderBudget budget)
    {
        if (!IsDeniedKey(key))
        {
            return ProcessAny(value, depth, budget);
        }

        // A null or empty value under a denied key is not "masked": the ordinary
        // "Request starting" line keeps its readable, formatted message when the query
        // string is empty.
        return value switch
        {
            null => new MaskResult(null, Masked: false),
            string { Length: 0 } empty => new MaskResult(empty, Masked: false),
            _ => new MaskResult(Mask, Masked: true),
        };
    }

    private MaskResult ProcessAny(object? value, int depth, RenderBudget budget)
    {
        if (value is null)
        {
            return new MaskResult(null, Masked: false);
        }

        if (value is string text)
        {
            var masked = MaskString(text, MaxStringLength, out var changed);
            return new MaskResult(masked, changed);
        }

        var type = value.GetType();

        if (SensitiveTypeAnalyzer.IsSensitiveType(type))
        {
            return new MaskResult(Mask, Masked: true);
        }

        if (value is Uri uri)
        {
            return new MaskResult(RenderUri(uri), Masked: true);
        }

        if (SensitiveTypeAnalyzer.IsScalar(type))
        {
            return new MaskResult(value, Masked: false);
        }

        if (SensitiveTypeAnalyzer.IsEnumerable(type))
        {
            if (!SensitiveTypeAnalyzer.HasScalarElements(type))
            {
                return new MaskResult(Mask, Masked: true);
            }

            // M-1 (G4-15-16, 17; G6 review): HasScalarElements proves the collection's
            // *shape* (string, Uri, or a safe scalar) is safe to pass through, not that its
            // string/Uri *contents* are. A List<string> or List<Uri> still needs the same
            // per-element MaskString/RenderUri treatment a top-level or member value gets.
            return SensitiveTypeAnalyzer.ElementsNeedShapeMasking(type) && value is IEnumerable enumerable
                ? ProcessScalarElements(enumerable)
                : new MaskResult(value, Masked: false);
        }

        if (!SensitiveTypeAnalyzer.NeedsProcessorRendering(type))
        {
            return new MaskResult(value, Masked: false);
        }

        if (!SensitiveTypeAnalyzer.IsProcessorRenderable(type) || depth > MaxRenderDepth || !budget.TryConsume())
        {
            return new MaskResult(Mask, Masked: true);
        }

        return new MaskResult(RenderObject(value, type, depth, budget), Masked: true);
    }

    /// <summary>
    /// M-1 (G4-15-16, 17; G6 review): per-element <c>MaskString</c>/<c>RenderUri</c> for a
    /// collection whose element type is <see cref="SensitiveTypeAnalyzer.IsShapeMaskableScalar"/>.
    /// A clean collection (no element <see cref="MaskString"/> or <see cref="RenderUri"/>
    /// would change) returns the original reference unchanged, so G4-15-11's "int[] and
    /// List&lt;string&gt; pass through" still holds by reference equality when there is
    /// nothing to mask.
    /// </summary>
    private static MaskResult ProcessScalarElements(IEnumerable enumerable)
    {
        var processed = new List<object?>();
        var anyChanged = false;

        foreach (var element in enumerable)
        {
            var (value, changed) = ProcessScalarElement(element);
            anyChanged |= changed;
            processed.Add(value);
        }

        return anyChanged ? new MaskResult(processed, Masked: true) : new MaskResult(enumerable, Masked: false);
    }

    private static (object? Value, bool Changed) ProcessScalarElement(object? element)
    {
        switch (element)
        {
            case null:
                return (null, false);
            case string text:
                var masked = MaskString(text, MaxStringLength, out var changed);
                return (masked, changed);
            case Uri uri:
                // RenderUri always counts as a change, mirroring the top-level rule: any Uri
                // is rendered through the processor, never handed to a sink's raw ToString().
                return (RenderUri(uri), true);
            default:
                return (element, false);
        }
    }

    private string RenderObject(object value, Type type, int depth, RenderBudget budget)
    {
        var builder = new StringBuilder("{ ");
        var properties = SensitiveTypeAnalyzer.GetRenderableProperties(type);
        var written = 0;
        var truncated = false;

        foreach (var property in properties)
        {
            if (written >= MaxMembersPerObject || !budget.TryConsume())
            {
                truncated = true;
                break;
            }

            if (written > 0)
            {
                builder.Append(", ");
            }

            builder.Append(property.Name).Append(" = ").Append(RenderMember(value, property, depth, budget));
            written++;
        }

        if (truncated)
        {
            if (written > 0)
            {
                builder.Append(", ");
            }

            builder.Append(Mask);
        }

        return builder.Append(" }").ToString();
    }

    private string RenderMember(object instance, PropertyInfo property, int depth, RenderBudget budget)
    {
        if (IsDeniedKey(property.Name) || SensitiveTypeAnalyzer.IsSensitiveMember(property))
        {
            return Mask;
        }

        object? raw;
        try
        {
            raw = property.GetValue(instance);
        }
        catch (Exception)
        {
            ObservabilityDiagnostics.RecordMaskingError("getter");
            return Mask;
        }

        return Format(ProcessAny(raw, depth + 1, budget).Value);
    }

    private static string Format(object? value)
    {
        switch (value)
        {
            case null:
                return "null";
            case string text:
                return text;
            case IFormattable formattable:
                try
                {
                    return formattable.ToString(null, CultureInfo.InvariantCulture) ?? Mask;
                }
                catch (Exception)
                {
                    ObservabilityDiagnostics.RecordMaskingError("format");
                    return Mask;
                }

            default:
                try
                {
                    return value.ToString() ?? Mask;
                }
                catch (Exception)
                {
                    ObservabilityDiagnostics.RecordMaskingError("format");
                    return Mask;
                }
        }
    }

    [GeneratedRegex(
        @"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]*",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex JwtShape();

    [GeneratedRegex(
        @"bearer\s+[A-Za-z0-9\-._~+/]+=*",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex BearerShape();

    private readonly record struct MaskResult(object? Value, bool Masked);

    /// <summary>
    /// Caps how many nodes the processor renders for one record, so a deeply nested or
    /// wide object cannot turn one log call into an unbounded amount of reflection.
    /// </summary>
    private sealed class RenderBudget
    {
        private int _remaining = MaxRenderedNodesPerRecord;

        internal bool TryConsume() => _remaining-- > 0;
    }
}
