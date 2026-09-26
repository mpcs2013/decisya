namespace Decisya.SharedKernel.Tenancy;

/// <summary>
/// Raised when text or a <see cref="Guid"/> cannot be resolved to a valid, non-empty
/// <see cref="TenantId"/>.
/// </summary>
/// <remarks>
/// Unlike <c>InvalidCurrencyCodeException</c>, this exception carries no raw-value property
/// and its <see cref="Exception.Message"/> echoes no part of the offending input, not even a
/// sanitised preview: a tenant identifier has no human-meaningful part worth echoing, and an
/// exception object that never holds the input can't leak it if it is later logged or
/// serialised (G1 Story 1; G3 T-05, G4-32-05).
/// </remarks>
public sealed class TenantIdFormatException : FormatException
{
    private const string FixedMessage = "The supplied value is not a valid tenant identifier.";

    /// <summary>Creates the exception with its fixed, input-free message.</summary>
    public TenantIdFormatException()
        : base(FixedMessage)
    {
    }
}
