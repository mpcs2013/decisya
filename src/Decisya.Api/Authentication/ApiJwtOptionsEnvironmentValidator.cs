using Microsoft.Extensions.Options;

namespace Decisya.Api.Authentication;

/// <summary>
/// G4-20-02 (T-05): the Development-only relaxations G2 allows — <c>Api:Jwt:RequireHttpsMetadata=false</c>
/// and a non-https <c>Api:Jwt:Authority</c> — must fail startup everywhere except Development.
/// DataAnnotations on <see cref="ApiJwtOptions"/> cannot see <see cref="IHostEnvironment"/>, so
/// this runs as a separate <see cref="IValidateOptions{TOptions}"/> registered alongside
/// <c>ValidateDataAnnotations()</c>, mirroring <c>Decisya.Bff.BffOptionsEnvironmentValidator</c>.
/// Every failure names the configuration key only, never the value.
/// </summary>
internal sealed class ApiJwtOptionsEnvironmentValidator(IHostEnvironment environment) : IValidateOptions<ApiJwtOptions>
{
    public ValidateOptionsResult Validate(string? name, ApiJwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (environment.IsDevelopment())
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (!IsHttpsAuthority(options.Authority))
        {
            failures.Add($"{ApiJwtOptions.SectionName}:Authority must be an absolute https URL outside Development.");
        }

        if (!options.RequireHttpsMetadata)
        {
            failures.Add($"{ApiJwtOptions.SectionName}:RequireHttpsMetadata may be false only in Development.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsHttpsAuthority(string? authority) =>
        Uri.TryCreate(authority, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
