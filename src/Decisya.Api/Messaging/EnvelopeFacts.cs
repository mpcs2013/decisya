using System.Diagnostics.CodeAnalysis;
using Decisya.SharedKernel.Tenancy;
using Wolverine;

namespace Decisya.Api.Messaging;

/// <summary>
/// The two facts an envelope carries for Decisya (ADR-0024 points 3 and 4): the tenant, as the
/// exact canonical text, in Wolverine's <c>Envelope.TenantId</c>, and the originating caller id in
/// the <see cref="CallerIdHeader"/> header. The send side writes them
/// (<see cref="EnvelopeTenantStamp"/>) and the receive side reads them
/// (<see cref="EnvelopeTenantMiddleware"/>), both through this type so the two ends can never
/// disagree about the shape.
/// </summary>
/// <remarks>
/// Message records carry no tenant property for routing: the envelope carries the tenant, and a
/// handler reads the tenant only from <see cref="ICurrentTenant"/>, never from a member of the
/// message (G3 S-02). A <see cref="TenantId"/> member that a message does carry is either a
/// constructor parameter or <see langword="required"/>, so a payload without it fails to
/// deserialize instead of producing <c>default(TenantId)</c>.
/// </remarks>
internal static class EnvelopeFacts
{
    /// <summary>The header that carries the originating caller id. It attributes work, never authorizes it.</summary>
    internal const string CallerIdHeader = "decisya-caller-id";

    /// <summary>The longest caller id the receive side accepts.</summary>
    internal const int MaxCallerIdLength = 128;

    /// <summary>
    /// The exact canonical tenant text or nothing. <see cref="TenantId.TryParse(ReadOnlySpan{char}, out TenantId)"/>
    /// alone trims whitespace and accepts upper-case hex, so the parsed value must also format back
    /// to the input, ordinally (ADR-0024 point 3).
    /// </summary>
    internal static bool TryReadTenant(string? text, out TenantId tenant, [NotNullWhen(false)] out EnvelopeRefusalReason? reason)
    {
        tenant = default;
        reason = null;

        if (string.IsNullOrEmpty(text))
        {
            reason = EnvelopeRefusalReason.Missing;
            return false;
        }

        if (!TenantId.TryParse(text.AsSpan(), out var parsed) || !string.Equals(parsed.ToString(), text, StringComparison.Ordinal))
        {
            reason = EnvelopeRefusalReason.Malformed;
            return false;
        }

        tenant = parsed;
        return true;
    }

    /// <summary>
    /// 1 to <see cref="MaxCallerIdLength"/> characters, no control character, no leading or
    /// trailing whitespace (G3 G4-231-02, T-06).
    /// </summary>
    internal static bool IsAcceptableCallerId([NotNullWhen(true)] string? callerId)
    {
        if (string.IsNullOrEmpty(callerId) || callerId.Length > MaxCallerIdLength)
        {
            return false;
        }

        if (char.IsWhiteSpace(callerId[0]) || char.IsWhiteSpace(callerId[^1]))
        {
            return false;
        }

        foreach (var c in callerId)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool TryReadCaller(Envelope envelope, [NotNullWhen(true)] out string? callerId)
    {
        callerId = null;
        if (envelope.Headers is { } headers && headers.TryGetValue(CallerIdHeader, out var value) && IsAcceptableCallerId(value))
        {
            callerId = value;
            return true;
        }

        return false;
    }
}
