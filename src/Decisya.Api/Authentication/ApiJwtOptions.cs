using System.ComponentModel.DataAnnotations;

namespace Decisya.Api.Authentication;

/// <summary>
/// Binds the <c>Api:Jwt</c> configuration section (G2). <c>Program</c> registers this with
/// <c>ValidateDataAnnotations().ValidateOnStart()</c>; <see cref="ApiJwtOptionsEnvironmentValidator"/>
/// adds the Development-only relaxation checks DataAnnotations cannot express. A missing or
/// invalid value stops startup and the failure message names the configuration key only,
/// never the value (CLAUDE.md).
/// </summary>
public sealed class ApiJwtOptions : IValidatableObject
{
    public const string SectionName = "Api:Jwt";

    /// <summary>The realm issuer, e.g. <c>https://localhost:8080/realms/decisya</c>. Required in
    /// every environment, as an absolute URI (G2). Never a hard-coded scheme or host
    /// (CLAUDE.md); read from configuration only.</summary>
    [Required(AllowEmptyStrings = false)]
    public string? Authority { get; init; }

    /// <summary>
    /// Honoured only when <c>IHostEnvironment.IsDevelopment()</c> is also true, and only for a
    /// loopback authority host (G2); see <see cref="ApiJwtOptionsEnvironmentValidator"/> and
    /// <see cref="JwtBearerOptionsSetup"/>.
    /// </summary>
    public bool RequireHttpsMetadata { get; init; } = true;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrWhiteSpace(Authority) && !Uri.TryCreate(Authority, UriKind.Absolute, out _))
        {
            yield return new ValidationResult(
                $"{SectionName}:Authority must be an absolute URI.", [nameof(Authority)]);
        }
    }
}
