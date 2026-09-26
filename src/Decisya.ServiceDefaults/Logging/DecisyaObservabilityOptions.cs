using Decisya.SharedKernel.Observability;

namespace Decisya.ServiceDefaults.Logging;

/// <summary>
/// Observability settings bound from the <c>Decisya:Observability</c> configuration
/// section. The only member today is the key that turns a raw user id into the hashed
/// <c>user_id</c> that logs carry.
/// </summary>
/// <remarks>
/// This is a class and not a record on purpose: a record's compiler-generated
/// <c>ToString</c> prints every member, so logging or dumping the options object would
/// print the key. <see cref="ToString"/> below is overridden to the type name, and
/// <see cref="UserIdHashKey"/> carries <see cref="SensitiveAttribute"/> so the logging
/// pipeline masks it even when the object reaches a log call.
/// </remarks>
public sealed class DecisyaObservabilityOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Decisya:Observability";

    /// <summary>The full configuration path of <see cref="UserIdHashKey"/>.</summary>
    public const string UserIdHashKeyPath = SectionName + ":UserIdHashKey";

    /// <summary>
    /// The environment-variable form of <see cref="UserIdHashKeyPath"/>, named in
    /// validation failures so an operator can act on them.
    /// </summary>
    public const string UserIdHashKeyEnvironmentVariable = "Decisya__Observability__UserIdHashKey";

    /// <summary>The minimum length, in bytes, of the decoded <see cref="UserIdHashKey"/>.</summary>
    public const int MinimumUserIdHashKeyBytes = 32;

    /// <summary>
    /// Base64-encoded HMAC-SHA256 key, at least <see cref="MinimumUserIdHashKeyBytes"/>
    /// bytes once decoded. It is a secret: it never appears in <c>appsettings*.json</c>,
    /// <c>launchSettings.json</c> or any other file in the repository (platform invariant
    /// 5). Outside Development a missing or malformed key stops the host from starting.
    /// In Development a per-process key is generated when the setting is absent.
    /// </summary>
    [Sensitive]
    public string? UserIdHashKey { get; set; }

    /// <summary>Never prints the key. See the remarks on the type.</summary>
    public override string ToString() => nameof(DecisyaObservabilityOptions);
}
