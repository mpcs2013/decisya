using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Decisya.ServiceDefaults.Logging;

/// <summary>
/// Fails the host start when <see cref="DecisyaObservabilityOptions.UserIdHashKey"/> is
/// missing or malformed outside Development (fail closed), and when it is present but
/// malformed in any environment, Development included.
/// </summary>
/// <remarks>
/// The failure messages name the setting and the rule and never echo the configured
/// value, so a key pasted into the wrong place does not end up in an
/// <c>OptionsValidationException</c>, its <c>ToString()</c> or a log line.
/// </remarks>
internal sealed class DecisyaObservabilityOptionsValidator(IHostEnvironment environment)
    : IValidateOptions<DecisyaObservabilityOptions>
{
    internal const string MissingKeyMessage =
        "Configuration setting '" + DecisyaObservabilityOptions.UserIdHashKeyPath + "' (environment variable " +
        DecisyaObservabilityOptions.UserIdHashKeyEnvironmentVariable + ") is required outside the Development " +
        "environment. Supply a base64-encoded key of at least 32 bytes. The value is never logged.";

    internal const string InvalidKeyMessage =
        "Configuration setting '" + DecisyaObservabilityOptions.UserIdHashKeyPath + "' (environment variable " +
        DecisyaObservabilityOptions.UserIdHashKeyEnvironmentVariable + ") must be base64 and decode to at least " +
        "32 bytes. The configured value was rejected and is never logged.";

    public ValidateOptionsResult Validate(string? name, DecisyaObservabilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.UserIdHashKey))
        {
            // In Development UserIdHashKeyProvisioner has already filled in a per-process
            // key by the time validation runs, so a blank value here means a non-Development
            // environment, or a Development host whose provisioner was removed.
            return environment.IsDevelopment()
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(MissingKeyMessage);
        }

        return IsAcceptableKey(options.UserIdHashKey)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(InvalidKeyMessage);
    }

    /// <summary>
    /// <see langword="true"/> when <paramref name="value"/> is base64 and decodes to at
    /// least <see cref="DecisyaObservabilityOptions.MinimumUserIdHashKeyBytes"/> bytes.
    /// Decoding uses <see cref="Convert.TryFromBase64String"/> rather than
    /// <c>Convert.FromBase64String</c>, so a non-base64 value can never escape as a
    /// <see cref="FormatException"/> carrying the value; it is reported as the generic
    /// rule instead.
    /// </summary>
    internal static bool IsAcceptableKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var buffer = new byte[((value.Length + 3) / 4) * 3];
        return Convert.TryFromBase64String(value, buffer, out var written)
            && written >= DecisyaObservabilityOptions.MinimumUserIdHashKeyBytes;
    }
}
