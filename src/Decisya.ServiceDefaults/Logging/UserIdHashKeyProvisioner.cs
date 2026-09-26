using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Decisya.ServiceDefaults.Logging;

/// <summary>
/// Generates a per-process <see cref="DecisyaObservabilityOptions.UserIdHashKey"/> in
/// Development when, and only when, the setting is absent or blank.
/// </summary>
/// <remarks>
/// <para>
/// The generated key is 32 bytes from <see cref="RandomNumberGenerator"/>. It is written
/// into the options instance only: never into <c>IConfiguration</c>, a file, an
/// environment variable or a log line. <c>user_id</c> hashes are therefore not
/// correlatable across hosts or restarts in Development, which is accepted.
/// </para>
/// <para>
/// A key that is <em>present but malformed</em> is left alone so that
/// <see cref="DecisyaObservabilityOptionsValidator"/> rejects it, in Development too.
/// </para>
/// </remarks>
internal sealed class UserIdHashKeyProvisioner(IHostEnvironment environment)
    : IPostConfigureOptions<DecisyaObservabilityOptions>
{
    private int _generated;

    /// <summary>
    /// <see langword="true"/> once a per-process key has been generated. Read by
    /// <see cref="EphemeralUserIdHashKeyWarning"/> to write exactly one startup warning.
    /// </summary>
    internal bool GeneratedEphemeralKey => Volatile.Read(ref _generated) != 0;

    public void PostConfigure(string? name, DecisyaObservabilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!environment.IsDevelopment() || !string.IsNullOrWhiteSpace(options.UserIdHashKey))
        {
            return;
        }

        options.UserIdHashKey = Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(DecisyaObservabilityOptions.MinimumUserIdHashKeyBytes));
        Volatile.Write(ref _generated, 1);
    }
}
