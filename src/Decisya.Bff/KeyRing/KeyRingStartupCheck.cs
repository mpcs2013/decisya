#pragma warning disable CA1031 // The check fails closed on any failure to read or unwrap a key; the detail is logged by rule.
using System.Globalization;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using NodaTime;

namespace Decisya.Bff.KeyRing;

/// <summary>What the check needs, fixed at registration so it matches the encryptor actually wired in.</summary>
internal sealed record KeyRingCheckSettings(bool Enabled, string? KeyRingPath, string? CurrentFingerprint, string? PreviousFingerprint);

/// <summary>Fixed-template events of the key ring (#122, G2 D11). Arguments come from closed lists or are a key id or a date.</summary>
internal static partial class KeyRingLog
{
    [LoggerMessage(
        EventId = 1830,
        EventName = "keyring.startup_check.failed",
        Level = LogLevel.Error,
        Message = "keyring.startup_check.failed: the Data Protection key ring failed its start-up check (rule {Rule}, key {KeyId}); the host will not start.")]
    internal static partial void StartupCheckFailed(ILogger logger, string rule, string keyId);

    [LoggerMessage(
        EventId = 1831,
        EventName = "keyring.previous_certificate",
        Level = LogLevel.Information,
        Message = "keyring.previous_certificate: latest expiration among keys that only the previous certificate decrypts: {LatestExpiration}. Retire the previous certificate after that date plus the session lifetime.")]
    internal static partial void PreviousCertificateInUse(ILogger logger, string latestExpiration);
}

/// <summary>
/// The fail-closed start-up check (#122, G2 D11, G3 G4-122-02). It runs in <c>StartingAsync</c>, which
/// precedes every <c>StartAsync</c>, including the framework's own Data Protection hosted service that
/// would otherwise create a fresh default key over an undecryptable ring (bff-session T-09). It only
/// reads: a failure leaves the volume untouched. Failure throws a generic message; the detail (rule, key
/// id, never key material) goes to the structured log.
/// </summary>
internal sealed class KeyRingStartupCheck(
    KeyRingCheckSettings settings, IServiceProvider services, IClock clock, ILoggerFactory loggerFactory)
    : IHostedLifecycleService
{
    internal const string GenericFailureMessage = "The Data Protection key ring failed its start-up check.";

    internal static readonly Duration SessionLifetime = Duration.FromHours(10);

    private const UnixFileMode ForbiddenDirectoryBits =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        if (!settings.Enabled)
        {
            return Task.CompletedTask;
        }

        var logger = loggerFactory.CreateLogger("Decisya.Bff.KeyRing");

        // Host start has no ambient trace; this span gives the failure line a trace id. No tags (no path, key id or certificate data).
        using var activity = Session.BffTelemetry.ActivitySource.StartActivity("keyring.startup_check");
        try
        {
            Run(logger);
        }
        catch (CheckFailedException failure)
        {
            KeyRingLog.StartupCheckFailed(logger, failure.Rule, failure.KeyId ?? "-");
            throw new InvalidOperationException(GenericFailureMessage);
        }

        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Run(ILogger logger)
    {
        var directory = CheckDirectory();
        var cutoff = clock.GetCurrentInstant() - SessionLifetime;

        // Rule 1: every live key decrypts. GetAllKeys reads and never creates; Descriptor throws for a key
        // the configured certificates cannot unwrap.
        IReadOnlyCollection<IKey> keys;
        try
        {
            keys = services.GetRequiredService<IKeyManager>().GetAllKeys();
        }
        catch (Exception)
        {
            throw new CheckFailedException("key_ring_unreadable", null);
        }

        foreach (var key in keys)
        {
            if (key.IsRevoked || Instant.FromDateTimeOffset(key.ExpirationDate) <= cutoff)
            {
                continue;
            }

            try
            {
                _ = key.Descriptor;
            }
            catch (Exception)
            {
                throw new CheckFailedException("key_undecryptable", key.KeyId.ToString("D"));
            }
        }

        // Rule 2: every top-level key file is certificate-wrapped, whatever its dates (NFR-54: 100%).
        DateTimeOffset? latestPreviousOnly = null;
        foreach (var path in Directory.EnumerateFiles(directory, "*.xml", SearchOption.TopDirectoryOnly))
        {
            var info = KeyRingFileRules.Inspect(path);
            if (info.Verdict != KeyFileVerdict.Ok)
            {
                throw new CheckFailedException(RuleOf(info.Verdict), info.KeyId);
            }

            if (settings.PreviousFingerprint is not null
                && info.Expiration is { } expiration
                && Instant.FromDateTimeOffset(expiration) > cutoff
                && info.WrappingFingerprints.Contains(settings.PreviousFingerprint)
                && (settings.CurrentFingerprint is null || !info.WrappingFingerprints.Contains(settings.CurrentFingerprint))
                && (latestPreviousOnly is null || expiration > latestPreviousOnly))
            {
                latestPreviousOnly = expiration;
            }
        }

        if (settings.PreviousFingerprint is not null)
        {
            var latest = latestPreviousOnly?.ToString("u", CultureInfo.InvariantCulture) ?? "none";
            KeyRingLog.PreviousCertificateInUse(logger, latest);
        }
    }

    private string CheckDirectory()
    {
        var path = settings.KeyRingPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            throw new CheckFailedException("directory_missing", null);
        }

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & ForbiddenDirectoryBits) != 0 || (mode & UnixFileMode.UserWrite) == 0)
            {
                throw new CheckFailedException("directory_mode", null);
            }
        }

        return path;
    }

    private static string RuleOf(KeyFileVerdict verdict) => verdict switch
    {
        KeyFileVerdict.PlaintextMaterial => "key_file_plaintext",
        KeyFileVerdict.NotCertificateWrapped => "key_file_not_certificate_wrapped",
        KeyFileVerdict.NotRegularFile => "key_file_not_regular",
        _ => "key_file_unreadable",
    };

    private sealed class CheckFailedException(string rule, string? keyId) : Exception(rule)
    {
        internal string Rule { get; } = rule;

        internal string? KeyId { get; } = keyId;
    }
}
