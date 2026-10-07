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

    [Required]
    public ApiOptions Api { get; init; } = new();

    public BackchannelOptions Backchannel { get; init; } = new();

    /// <summary>#120 G4-120-05 (B-3): trust for the BFF's back-channel clients (OIDC discovery,
    /// JWKS and token calls, and the YARP forwarder to the Api).</summary>
    public sealed class BackchannelOptions
    {
        /// <summary>
        /// Path of the one PEM CA certificate the back-channel clients trust (the stack mounts
        /// Caddy's exported root at <c>/etc/decisya/trust/caddy-root.crt</c>). Unset means
        /// system trust (Development, or a public CA). When set, the file must exist and hold
        /// exactly one CA certificate, or start-up fails (<c>BackchannelTrustOptionsValidator</c>).
        /// </summary>
        public string? TrustedRootPath { get; init; }
    }

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

    /// <summary>#19 G2: the single YARP cluster destination. <see cref="Address"/> is resolved
    /// through service discovery (<c>https://decisya-api</c> in the AppHost); outside
    /// Development it must be an absolute <c>https</c> URL
    /// (<see cref="BffOptionsEnvironmentValidator"/>), the same pattern as
    /// <see cref="OidcOptions.RequireHttpsMetadata"/>. This is defence in depth only: the
    /// per-request final-URI check in <c>Decisya.Bff.Proxy</c> is what actually guards T-02,
    /// because service discovery can still resolve this address to a different scheme.</summary>
    public sealed class ApiOptions
    {
        [Required(AllowEmptyStrings = false)]
        public string? Address { get; init; }
    }
}
