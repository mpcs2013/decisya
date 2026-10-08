using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace Decisya.Bff.KeyRing;

/// <summary>
/// Loads and checks the key-ring wrapping certificate (#122, G2 D10): standard base64 of a PKCS#12 with
/// one RSA key of at least 3072 bits and its password, loaded with <c>EphemeralKeySet</c> so nothing is
/// written to the read-only root file system or a user key store. A failure returns
/// <see langword="false"/>; no message, exception text or value leaves this type.
/// </summary>
internal static class KeyRingCertificates
{
    internal const int MinimumRsaBits = 3072;
    internal const int MaxPfxBytes = 16 * 1024;

    internal static bool TryLoad(string? base64, string? password, out X509Certificate2? certificate)
    {
        certificate = null;
        if (string.IsNullOrWhiteSpace(base64) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        var buffer = new byte[MaxPfxBytes];
        if (!Convert.TryFromBase64String(base64.Trim(), buffer, out var written) || written == 0)
        {
            return false;
        }

        X509Certificate2? loaded = null;
        try
        {
            loaded = X509CertificateLoader.LoadPkcs12(
                buffer.AsSpan(0, written), password, X509KeyStorageFlags.EphemeralKeySet);
            using var rsa = loaded.GetRSAPublicKey();
            if (!loaded.HasPrivateKey || rsa is null || rsa.KeySize < MinimumRsaBits)
            {
                loaded.Dispose();
                return false;
            }

            certificate = loaded;
            return true;
        }
        catch (CryptographicException)
        {
            loaded?.Dispose();
            return false;
        }
    }

    internal static string Fingerprint(X509Certificate2 certificate) =>
        certificate.GetCertHashString(HashAlgorithmName.SHA256);
}

/// <summary>
/// #122 G2 D10: outside Development the certificate pair is required (fail closed, Q4); in every
/// environment, a value that is set must load, hold an RSA key of at least 3072 bits and, for the
/// previous pair, differ from the current one. Messages name the key and never a value.
/// </summary>
internal sealed class KeyRingOptionsValidator(IHostEnvironment environment) : IValidateOptions<BffOptions>
{
    private const string Prefix = "Bff:DataProtection:";

    public ValidateOptionsResult Validate(string? name, BffOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var dp = options.DataProtection;
        var failures = new List<string>();
        var haveCurrent = !string.IsNullOrWhiteSpace(dp.Certificate);

        if (!environment.IsDevelopment())
        {
            if (!haveCurrent)
            {
                failures.Add(Prefix + "Certificate must be set outside Development.");
            }

            if (string.IsNullOrEmpty(dp.CertificatePassword))
            {
                failures.Add(Prefix + "CertificatePassword must be set outside Development.");
            }
        }

        X509Certificate2? current = null;
        X509Certificate2? previous = null;
        try
        {
            if (haveCurrent || !string.IsNullOrEmpty(dp.CertificatePassword))
            {
                if (!KeyRingCertificates.TryLoad(dp.Certificate, dp.CertificatePassword, out current))
                {
                    failures.Add(Prefix + "Certificate and " + Prefix + "CertificatePassword must be a base64 PKCS#12 with its password holding an RSA key of at least 3072 bits.");
                }
            }

            var havePrevious = !string.IsNullOrWhiteSpace(dp.PreviousCertificate);
            var havePreviousPassword = !string.IsNullOrEmpty(dp.PreviousCertificatePassword);
            if (havePrevious != havePreviousPassword)
            {
                failures.Add(Prefix + "PreviousCertificate and " + Prefix + "PreviousCertificatePassword must both be set or both be empty.");
            }
            else if (havePrevious)
            {
                if (!KeyRingCertificates.TryLoad(dp.PreviousCertificate, dp.PreviousCertificatePassword, out previous))
                {
                    failures.Add(Prefix + "PreviousCertificate and " + Prefix + "PreviousCertificatePassword must be a base64 PKCS#12 with its password holding an RSA key of at least 3072 bits.");
                }
                else if (current is not null
                    && string.Equals(KeyRingCertificates.Fingerprint(previous!), KeyRingCertificates.Fingerprint(current), StringComparison.Ordinal))
                {
                    failures.Add(Prefix + "PreviousCertificate must differ from " + Prefix + "Certificate.");
                }
            }
        }
        finally
        {
            current?.Dispose();
            previous?.Dispose();
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
