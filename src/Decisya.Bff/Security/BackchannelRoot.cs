using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Decisya.Bff.Security;

/// <summary>
/// #120 G4-120-05 (B-3): the one place the BFF decides what its back-channel clients trust.
/// When <c>Bff:Backchannel:TrustedRootPath</c> is set, each back-channel client (the OIDC
/// discovery, JWKS and token calls, and the YARP forwarder to the Api) validates the server
/// certificate against that one mounted root and nothing else: <c>CustomRootTrust</c> on the
/// handler's own chain policy, so the operating-system store, <c>SSL_CERT_FILE</c> and every
/// other client in the process are unaffected. Host name validation still applies.
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately no custom certificate validation callback anywhere in the code base
/// (a source rule in the test project enforces it): a callback is one wrong boolean away from
/// accepting every certificate, while a chain policy can only ever narrow trust.
/// </para>
/// <para>
/// Failure messages name the configuration key only, never the path or the file content.
/// </para>
/// </remarks>
internal static class BackchannelRoot
{
    // A root certificate is about 1 KB. Anything near this limit is not one.
    private const int MaximumFileBytes = 64 * 1024;

    /// <summary>
    /// Reads the root from <paramref name="path"/>. Returns <see langword="null"/> when the path
    /// is unset (system trust applies, the Development and public-CA case). When it is set, the
    /// file must exist and hold exactly one PEM certificate that is a CA; anything else throws
    /// <see cref="InvalidOperationException"/> naming <paramref name="key"/>.
    /// </summary>
    internal static X509Certificate2? LoadOrNull(string? path, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string text;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                throw Fail(key, "the file does not exist");
            }

            if (file.Length > MaximumFileBytes)
            {
                throw Fail(key, "the file is too large to be one certificate");
            }

            text = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw Fail(key, "the file cannot be read");
        }

        return ParseSingleCaCertificate(text, key);
    }

    /// <summary>Parses PEM text that must hold exactly one CA certificate and nothing secret.</summary>
    internal static X509Certificate2 ParseSingleCaCertificate(string pemText, string key)
    {
        ArgumentNullException.ThrowIfNull(pemText);

        var certificates = new List<X509Certificate2>();
        var remaining = pemText.AsSpan();
        while (PemEncoding.TryFind(remaining, out var fields))
        {
            // A private key or any other block in this file means the wrong file was mounted
            // (Caddy's data volume holds root.key next to root.crt). Refuse it outright.
            if (!remaining[fields.Label].SequenceEqual("CERTIFICATE"))
            {
                throw Fail(key, "the file holds something other than a public certificate");
            }

            try
            {
                var der = Convert.FromBase64String(remaining[fields.Base64Data].ToString());
                certificates.Add(X509CertificateLoader.LoadCertificate(der));
            }
            catch (Exception exception) when (exception is FormatException or CryptographicException)
            {
                throw Fail(key, "the certificate is malformed");
            }

            remaining = remaining[fields.Location.End..];
        }

        if (certificates.Count != 1)
        {
            throw Fail(key, "the file must hold exactly one certificate");
        }

        var certificate = certificates[0];
        var basicConstraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
        if (basicConstraints is not { CertificateAuthority: true })
        {
            throw Fail(key, "the certificate is not a CA certificate");
        }

        return certificate;
    }

    /// <summary>The chain policy a back-channel client uses: the one root, nothing else.</summary>
    internal static X509ChainPolicy CreateChainPolicy(X509Certificate2 root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            // Caddy's internal CA publishes no CRL or OCSP responder, and its leaves live 12 hours.
            RevocationMode = X509RevocationMode.NoCheck,
            // Never fetch an intermediate from a URL named inside a certificate.
            DisableCertificateDownloads = true,
        };
        policy.CustomTrustStore.Add(root);
        return policy;
    }

    /// <summary>Pins <paramref name="handler"/>'s server certificate validation to <paramref name="root"/>.</summary>
    internal static void Apply(SocketsHttpHandler handler, X509Certificate2 root)
    {
        ArgumentNullException.ThrowIfNull(handler);

        handler.SslOptions.CertificateChainPolicy = CreateChainPolicy(root);
    }

    /// <summary>A fresh handler pinned to <paramref name="root"/>, for one back-channel client.</summary>
    internal static SocketsHttpHandler CreateHandler(X509Certificate2 root)
    {
        var handler = new SocketsHttpHandler { SslOptions = new SslClientAuthenticationOptions() };
        Apply(handler, root);
        return handler;
    }

    private static InvalidOperationException Fail(string key, string reason) =>
        new($"{key} is set but invalid: {reason}. It must name a PEM file holding exactly one CA certificate.");
}
