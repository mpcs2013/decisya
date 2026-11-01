namespace Decisya.Api.Messaging;

/// <summary>
/// Why <see cref="EnvelopeTenantMiddleware"/> refused a message (ADR-0024 point 3). A closed set:
/// the only metric tag and the only reason word in the log event.
/// </summary>
internal enum EnvelopeRefusalReason
{
    /// <summary>The envelope carries no tenant at all (absent or the empty string).</summary>
    Missing,

    /// <summary>
    /// The envelope tenant is not the exact canonical text of an initialized tenant: whitespace,
    /// <c>not-a-guid</c>, the all-zero GUID, padding, upper case, or Wolverine's <c>*DEFAULT*</c>.
    /// </summary>
    Malformed,

    /// <summary>
    /// The tenant is valid but the caller id is absent, empty, over 128 characters, or holds a
    /// control character or leading or trailing whitespace (G3 G4-231-02).
    /// </summary>
    NoCaller,
}

internal static class EnvelopeRefusalReasons
{
    internal const string Missing = "missing";
    internal const string Malformed = "malformed";
    internal const string NoCaller = "no-caller";

    internal static string Text(EnvelopeRefusalReason reason) => reason switch
    {
        EnvelopeRefusalReason.Missing => Missing,
        EnvelopeRefusalReason.Malformed => Malformed,
        EnvelopeRefusalReason.NoCaller => NoCaller,
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}
