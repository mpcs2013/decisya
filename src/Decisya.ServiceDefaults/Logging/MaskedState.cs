namespace Decisya.ServiceDefaults.Logging;

/// <summary>
/// The result of running one log record's state through
/// <see cref="SensitiveDataMaskingProcessor"/>. Both sinks consume it, so stdout and OTLP
/// cannot drift apart.
/// </summary>
/// <param name="Attributes">
/// The masked key/value pairs, without the <c>{OriginalFormat}</c> pair. A
/// <c>decisya.masking_error</c> pair is appended when a rule failed or the state was
/// unstructured.
/// </param>
/// <param name="AnyMasked">
/// <see langword="true"/> when <em>any</em> value was replaced, truncated or rendered by
/// the processor. It is <see langword="false"/> only when every value passed through
/// unchanged. When it is <see langword="true"/>, sinks emit the raw message template
/// instead of the Microsoft.Extensions.Logging-formatted message, because that formatted
/// message would contain the unmasked values.
/// </param>
/// <param name="Template">
/// The <c>{OriginalFormat}</c> value, or <see langword="null"/> when the state carried
/// none (rule 7, unstructured state).
/// </param>
public readonly record struct MaskedState(
    IReadOnlyList<KeyValuePair<string, object?>> Attributes,
    bool AnyMasked,
    string? Template);
