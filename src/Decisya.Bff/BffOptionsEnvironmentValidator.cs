using Microsoft.Extensions.Options;

namespace Decisya.Bff;

/// <summary>
/// G4-18-04 (T-08, T-09): the three Development-only relaxations G2 allows —
/// <c>Bff:Oidc:RequireHttpsMetadata=false</c>, a non-https <c>Bff:Oidc:Authority</c>, and an
/// unset <c>Bff:DataProtection:KeyRingPath</c> — must fail startup everywhere except
/// Development. DataAnnotations on <see cref="BffOptions"/> cannot see
/// <see cref="IHostEnvironment"/>, so this runs as a separate <see cref="IValidateOptions{TOptions}"/>
/// registered alongside <c>ValidateDataAnnotations()</c>. Every failure names the
/// configuration key only, never the value (CLAUDE.md: "the error message names the key,
/// never the value").
/// </summary>
internal sealed class BffOptionsEnvironmentValidator(IHostEnvironment environment) : IValidateOptions<BffOptions>
{
    public ValidateOptionsResult Validate(string? name, BffOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (environment.IsDevelopment())
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (!options.Oidc.RequireHttpsMetadata)
        {
            failures.Add("Bff:Oidc:RequireHttpsMetadata may be false only in Development.");
        }

        if (!IsHttpsAuthority(options.Oidc.Authority))
        {
            failures.Add("Bff:Oidc:Authority must be an absolute https URL outside Development.");
        }

        if (string.IsNullOrWhiteSpace(options.DataProtection.KeyRingPath))
        {
            failures.Add("Bff:DataProtection:KeyRingPath must be set outside Development.");
        }

        if (!IsHttpsAuthority(options.Api.Address))
        {
            // G3 point 2 (T-02): the configured string only. The final-URI check on the
            // resolved destination lives in Decisya.Bff.Proxy, because service discovery can
            // still resolve this to a non-https endpoint.
            failures.Add("Bff:Api:Address must be an absolute https URL outside Development.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsHttpsAuthority(string? authority) =>
        Uri.TryCreate(authority, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
