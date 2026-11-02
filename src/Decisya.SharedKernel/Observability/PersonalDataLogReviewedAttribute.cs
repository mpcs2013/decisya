namespace Decisya.SharedKernel.Observability;

/// <summary>
/// Records that a reviewer accepted one personal-data log finding in the method or constructor that carries
/// it (issue #230, G2 D4). <c>PersonalDataLogRule</c> in <c>Decisya.ArchitectureTests</c> reads it from the
/// built assembly. It suppresses only <see cref="Category"/>, only in this method. Every use must also be
/// pinned in the <c>suppressions</c> array of <c>docs/security/personal-data-catalogue.json</c>
/// (security-owned), or the architecture test fails. A category the catalogue marks
/// <c>"suppressible": false</c> (secret material) can never be suppressed.
/// </summary>
/// <remarks>
/// The constructor deliberately never throws, as <c>AllowCrossTenantAttribute</c> does: a throwing attribute
/// constructor breaks reflection over the whole assembly. The architecture test rejects a blank or short
/// justification and an unknown category by reading the constructor arguments. The justification holds no
/// personal data.
/// </remarks>
/// <param name="category">The catalogue category id being accepted (for example <c>free-text</c>).</param>
/// <param name="justification">Why the finding is a verified false positive (at least 20 characters).</param>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor, AllowMultiple = true, Inherited = false)]
public sealed class PersonalDataLogReviewedAttribute(string category, string justification) : Attribute
{
    /// <summary>The catalogue category id the reviewer accepted.</summary>
    public string Category { get; } = category;

    /// <summary>The reviewer's justification, exactly as declared.</summary>
    public string Justification { get; } = justification;
}
