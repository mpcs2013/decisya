using Microsoft.Extensions.Options;

namespace Decisya.Api.Authentication;

/// <summary>
/// <c>Authentication:RequireAdminMfa</c> (issue #121, G3 G4-121-01 d): whether <c>/api/admin</c>
/// requires the MFA proof in the access token. Default <b>true</b>. It may be false only in
/// Development (the dev realm has no OTP step); <see cref="AdminMfaOptionsEnvironmentValidator"/>
/// fails start-up everywhere else. The stack never sets the key (stackguards guard g), so
/// production runs on the default.
/// </summary>
internal sealed class AdminMfaOptions
{
    internal const string SectionName = "Authentication";

    /// <summary>The configuration key, as named in validation messages (never its value).</summary>
    internal const string RequireAdminMfaKey = "Authentication:RequireAdminMfa";

    /// <summary>Fail-closed default.</summary>
    public bool RequireAdminMfa { get; set; } = true;

    /// <summary>
    /// True when the key was present but is not a boolean. Kept as a flag, not rethrown from the
    /// binder, so the failure message names the key and never echoes the value.
    /// </summary>
    public bool ValueIsNotABoolean { get; set; }
}

/// <summary>Binds <see cref="AdminMfaOptions"/> from configuration without a binder exception that would quote the value.</summary>
internal sealed class AdminMfaOptionsSetup(IConfiguration configuration) : IConfigureOptions<AdminMfaOptions>
{
    public void Configure(AdminMfaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var raw = configuration[AdminMfaOptions.RequireAdminMfaKey];
        if (raw is null)
        {
            options.RequireAdminMfa = true;
            return;
        }

        if (bool.TryParse(raw, out var value))
        {
            options.RequireAdminMfa = value;
            return;
        }

        // Fail closed in the meantime; the validator turns the flag into a start-up failure.
        options.RequireAdminMfa = true;
        options.ValueIsNotABoolean = true;
    }
}

/// <summary>
/// Fails start-up when <c>Authentication:RequireAdminMfa</c> is not a boolean (everywhere), or is
/// false outside <c>Development</c>. Names the key only. Mirrors <see cref="ApiJwtOptionsEnvironmentValidator"/>.
/// </summary>
internal sealed class AdminMfaOptionsEnvironmentValidator(IHostEnvironment environment) : IValidateOptions<AdminMfaOptions>
{
    public ValidateOptionsResult Validate(string? name, AdminMfaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.ValueIsNotABoolean)
        {
            failures.Add($"{AdminMfaOptions.RequireAdminMfaKey} must be true or false.");
        }

        if (!options.RequireAdminMfa && !environment.IsDevelopment())
        {
            failures.Add($"{AdminMfaOptions.RequireAdminMfaKey} may be false only in Development.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
