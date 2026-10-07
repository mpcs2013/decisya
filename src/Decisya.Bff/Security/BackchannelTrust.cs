using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace Decisya.Bff.Security;

/// <summary>
/// #120 G4-120-05: hands each back-channel client its own handler, pinned to the mounted root
/// when <see cref="BffOptions.BackchannelOptions.TrustedRootPath"/> is set, and gives out
/// nothing when it is not (the client keeps its default, system trust). Reads the options
/// through <see cref="IOptionsMonitor{TOptions}"/> at use, so it never captures a snapshot;
/// <see cref="BackchannelTrustOptionsValidator"/> has already failed start-up for a bad file.
/// </summary>
internal sealed class BackchannelTrust(IOptionsMonitor<BffOptions> options)
{
    internal const string TrustedRootPathKey = "Bff:Backchannel:TrustedRootPath";

    /// <summary>The configured root, or <see langword="null"/> when none is configured.</summary>
    internal X509Certificate2? LoadRoot() =>
        BackchannelRoot.LoadOrNull(options.CurrentValue.Backchannel.TrustedRootPath, TrustedRootPathKey);

    /// <summary>A new pinned handler for one client, or <see langword="null"/> when none is configured.</summary>
    internal SocketsHttpHandler? CreateHandlerOrNull() =>
        LoadRoot() is { } root ? BackchannelRoot.CreateHandler(root) : null;

    /// <summary>Pins <paramref name="handler"/> to the configured root, if there is one.</summary>
    internal void ApplyTo(SocketsHttpHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        if (LoadRoot() is { } root)
        {
            BackchannelRoot.Apply(handler, root);
        }
    }
}

/// <summary>Fails start-up (through <c>ValidateOnStart</c>) when the trusted-root file is set and
/// missing, unreadable, malformed, not exactly one certificate, or not a CA. Runs in every
/// environment: a root that is configured must be usable, in Development too.</summary>
internal sealed class BackchannelTrustOptionsValidator : IValidateOptions<BffOptions>
{
    public ValidateOptionsResult Validate(string? name, BffOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            using var root = BackchannelRoot.LoadOrNull(
                options.Backchannel.TrustedRootPath, BackchannelTrust.TrustedRootPathKey);
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException exception)
        {
            return ValidateOptionsResult.Fail(exception.Message);
        }
    }
}
