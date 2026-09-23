using System.Text;

namespace Decisya.SharedKernel;

/// <summary>
/// Raised when a <see cref="Currency"/> is constructed from a code that is not a
/// recognised, active ISO 4217 alphabetic currency code.
/// </summary>
public sealed class InvalidCurrencyCodeException : ArgumentException
{
    /// <summary>
    /// Longest sanitised preview <see cref="Exception.Message"/> ever echoes back; a
    /// legitimate currency code is 2-4 characters, so this is generous headroom, not a
    /// promise the whole (possibly much longer) input fits.
    /// </summary>
    private const int MaxMessagePreviewLength = 16;

    public InvalidCurrencyCodeException(string? code)
        : base(BuildMessage(code), nameof(code))
    {
        Code = code;
    }

    /// <summary>
    /// The offending, unrecognised code, exactly as supplied. Unlike
    /// <see cref="Exception.Message"/>, this is not sanitised or truncated: a caller
    /// that needs the raw value (e.g. to log it through a masking pipeline) can read it
    /// here, but must not put it, unsanitised, somewhere an untrusted party can read it
    /// back (e.g. ProblemDetails, or an unstructured log line).
    /// </summary>
    public string? Code { get; }

    /// <summary>
    /// Builds a message that never echoes the raw input verbatim: only a short,
    /// truncated, printable-ASCII-only preview (or none at all), so a malformed or
    /// hostile code — CRLF, embedded JSON, or an unbounded string — can never inflate a
    /// log line or leak into a client-facing message via <see cref="Exception.Message"/>.
    /// </summary>
    private static string BuildMessage(string? code)
    {
        var preview = Sanitize(code);
        return preview is null
            ? "The supplied currency code is not a recognised ISO 4217 currency code."
            : $"'{preview}' is not a recognised ISO 4217 currency code.";
    }

    private static string? Sanitize(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return null;
        }

        var builder = new StringBuilder(MaxMessagePreviewLength + 1);
        foreach (var c in code)
        {
            if (builder.Length >= MaxMessagePreviewLength)
            {
                builder.Append('…'); // ellipsis: marks the preview as truncated.
                break;
            }

            // Printable ASCII only (space through '~'): drops control characters
            // (including CR/LF) and any non-ASCII character rather than echoing them.
            if (c is >= (char)0x20 and <= (char)0x7E)
            {
                builder.Append(c);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }
}
