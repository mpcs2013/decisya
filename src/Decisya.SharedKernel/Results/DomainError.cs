using System.Text.Json.Serialization;

namespace Decisya.SharedKernel.Results;

/// <summary>
/// One expected failure: a stable, machine-readable <see cref="Code"/>, a
/// <see cref="Category"/> for a later HTTP-status mapping, and a developer-facing
/// <see cref="Message"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Message"/> is developer-facing detail for the structured log: describe the
/// failure precisely (a constraint name, an internal state, an entity id, never a user id or
/// other personal identifier), but never put raw user input, PII, a token, a secret or a row
/// value in it. Nothing in <c>Decisya.SharedKernel</c>
/// ever sends <see cref="Message"/> — or this type — to an HTTP response;
/// <see cref="Message"/> is <see cref="JsonIgnoreAttribute">JSON-ignored</see> and
/// <see cref="ToString"/> never renders it, as defence in depth. Issue #20 maps only
/// <see cref="Code"/> and <see cref="Category"/> into a client-facing <c>ProblemDetails</c>
/// response, per the platform's "generic message to the client, full detail to the
/// structured log with trace id" principle.
/// </para>
/// </remarks>
public sealed class DomainError : IEquatable<DomainError>
{
    /// <summary>The longest <see cref="Code"/> <see cref="New"/> accepts.</summary>
    public const int MaxCodeLength = 100;

    private DomainError(string code, string message, ErrorCategory category)
    {
        Code = code;
        Message = message;
        Category = category;
    }

    /// <summary>
    /// A stable, machine-readable, lower-case, dot-separated identifier (e.g.
    /// <c>"tenant.not_found"</c>). This is the one member of <see cref="DomainError"/> a
    /// future <c>ProblemDetails</c> mapping may put in a client-facing response.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Developer-facing detail for the structured log. See the type-level remarks for what
    /// this may and must never contain.
    /// </summary>
    [JsonIgnore]
    public string Message { get; }

    /// <summary>A coarse classification of the failure, for a later HTTP-status mapping.</summary>
    public ErrorCategory Category { get; }

    /// <summary>
    /// Creates a <see cref="DomainError"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> or <paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="code"/> is empty, whitespace-only, longer than <see cref="MaxCodeLength"/>,
    /// or does not match <c>^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)*$</c>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="category"/> is not a defined <see cref="ErrorCategory"/> value.</exception>
    public static DomainError New(string code, string message, ErrorCategory category = ErrorCategory.Failure)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(message);

        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "The error category is not a recognised ErrorCategory value.");
        }

        if (!IsValidCode(code))
        {
            throw new ArgumentException(
                "The error code must be a non-empty, lower-case, dot-separated identifier matching ^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)*$.",
                nameof(code));
        }

        return new DomainError(code, message, category);
    }

    /// <summary>
    /// Validates <paramref name="code"/> against <c>^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)*$</c>
    /// as a plain ASCII character loop, not a <see cref="System.Text.RegularExpressions.Regex"/>.
    /// </summary>
    private static bool IsValidCode(string code)
    {
        if (code.Length == 0 || code.Length > MaxCodeLength)
        {
            return false;
        }

        var atSegmentStart = true;

        foreach (var c in code)
        {
            if (c == '.')
            {
                // A dot can neither start a segment (including the whole code) nor
                // immediately follow another dot.
                if (atSegmentStart)
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

        // The code must not end with a trailing, segment-less dot.
        return !atSegmentStart;
    }

    /// <summary>Equal <see cref="Code"/> (ordinal) and <see cref="Category"/>; <see cref="Message"/> is ignored.</summary>
    public bool Equals(DomainError? other) =>
        other is not null
        && string.Equals(Code, other.Code, StringComparison.Ordinal)
        && Category == other.Category;

    public override bool Equals(object? obj) => obj is DomainError other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(string.GetHashCode(Code, StringComparison.Ordinal), Category);

    /// <summary>Never renders <see cref="Message"/>.</summary>
    public override string ToString() => $"{Code} ({Category})";

    public static bool operator ==(DomainError? left, DomainError? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        return left is not null && left.Equals(right);
    }

    public static bool operator !=(DomainError? left, DomainError? right) => !(left == right);
}
