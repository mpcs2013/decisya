using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Decisya.ServiceDefaults.Production;

/// <summary>
/// The production-only hosting values that must fail closed (issue #120, D6): the
/// <c>AllowedHosts</c> list is set outside Development and is never the wildcard.
/// </summary>
internal sealed class ProductionHostingOptions
{
    /// <summary>The root configuration key the ASP.NET Core host-filtering middleware reads.</summary>
    internal const string AllowedHostsKey = "AllowedHosts";

    /// <summary>The raw <c>AllowedHosts</c> value (semicolon-separated), or <see langword="null"/>.</summary>
    public string? AllowedHosts { get; set; }
}

internal sealed class ProductionHostingOptionsValidator(IHostEnvironment environment)
    : IValidateOptions<ProductionHostingOptions>
{
    public ValidateOptionsResult Validate(string? name, ProductionHostingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (environment.IsDevelopment())
        {
            return ValidateOptionsResult.Success;
        }

        return ValidateAllowedHosts(options.AllowedHosts);
    }

    internal static ValidateOptionsResult ValidateAllowedHosts(string? allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(allowedHosts))
        {
            return ValidateOptionsResult.Fail(
                $"{ProductionHostingOptions.AllowedHostsKey} must be set outside Development " +
                "(the public host name this service answers to).");
        }

        var entries = allowedHosts.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length == 0)
        {
            return ValidateOptionsResult.Fail(
                $"{ProductionHostingOptions.AllowedHostsKey} must list at least one host name outside Development.");
        }

        // "*" admits every Host header. "*.example" admits every sub-domain; that is also
        // refused, because a production service answers to named hosts only.
        if (entries.Any(static entry => entry.Contains('*', StringComparison.Ordinal)))
        {
            return ValidateOptionsResult.Fail(
                $"{ProductionHostingOptions.AllowedHostsKey} must not contain a wildcard outside Development.");
        }

        return ValidateOptionsResult.Success;
    }
}
