using System.ComponentModel.DataAnnotations;

namespace Decisya.Bff;

/// <summary>
/// Binds the <c>Bff</c> configuration section (G2). <see cref="Program"/> registers this
/// with <c>ValidateDataAnnotations().ValidateOnStart()</c>; <see cref="BffOptionsEnvironmentValidator"/>
/// adds the Development-only relaxation checks DataAnnotations cannot express
/// (G4-18-04). A missing or invalid value stops startup and the failure message names the
/// configuration key only, never the value.
/// </summary>
public sealed class BffOptions
{
    public const string SectionName = "Bff";

    [Required]
    public OidcOptions Oidc { get; init; } = new();

    public DataProtectionOptions DataProtection { get; init; } = new();

    public sealed class OidcOptions
    {
        /// <summary>The realm issuer, e.g. <c>https://localhost:8080/realms/decisya</c>. Never a
        /// hard-coded scheme or host (CLAUDE.md); read from configuration only.</summary>
        [Required(AllowEmptyStrings = false)]
        public string? Authority { get; init; }

        [Required(AllowEmptyStrings = false)]
        public string? ClientId { get; init; }

        [Required(AllowEmptyStrings = false)]
        public string? ClientSecret { get; init; }

        /// <summary>
        /// Honoured only when <c>IHostEnvironment.IsDevelopment()</c> is also true (G2, T-08);
        /// see <see cref="BffOptionsEnvironmentValidator"/>.
        /// </summary>
        public bool RequireHttpsMetadata { get; init; } = true;
    }

    public sealed class DataProtectionOptions
    {
        /// <summary>
        /// Required outside Development (<see cref="BffOptionsEnvironmentValidator"/>); left
        /// unset in Development so the framework's own per-user default
        /// (<c>%LOCALAPPDATA%\ASP.NET\DataProtection-Keys</c>, DPAPI-protected) applies (D1).
        /// </summary>
        public string? KeyRingPath { get; init; }
    }
}
