using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Decisya.TestSupport.Tls;

/// <summary>
/// #120 G4-120-05 test support: a throwaway certificate authority and the leaf certificates it
/// issues, generated in memory per test. Linked as source into <c>Decisya.Bff.Tests</c> and
/// <c>Decisya.Api.Tests</c> (the same way <c>ContainerImages.cs</c> is), so both projects prove
/// the same trust behaviour with the same generator. Nothing here is a real secret and nothing
/// is written outside a caller's temporary directory.
/// </summary>
internal sealed class TlsTestPki : IDisposable
{
    private readonly ECDsa _rootKey;

    private TlsTestPki(string name)
    {
        _rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", _rootKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        Root = request.CreateSelfSigned(Now().AddHours(-1), Now().AddDays(1));
    }

    /// <summary>The CA certificate (with its private key, for issuing only).</summary>
    internal X509Certificate2 Root { get; }

    /// <summary>The CA certificate as PEM: public, the same shape as Caddy's exported <c>root.crt</c>.</summary>
    internal string RootPem => Root.ExportCertificatePem();

    internal static TlsTestPki Create(string name) => new(name);

    /// <summary>A server certificate valid now, chaining to <see cref="Root"/>.</summary>
    internal X509Certificate2 IssueServerCertificate(string[] dnsNames, bool includeLoopbackIp = true)
    {
        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=decisya-test-server", leafKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        var names = new SubjectAlternativeNameBuilder();
        foreach (var dnsName in dnsNames)
        {
            names.AddDnsName(dnsName);
        }

        if (includeLoopbackIp)
        {
            names.AddIpAddress(IPAddress.Loopback);
        }

        request.CertificateExtensions.Add(names.Build());

        var serial = RandomNumberGenerator.GetBytes(8);
        serial[0] &= 0x7F;
        serial[0] |= 0x01;

        using var issued = request.Create(Root, Now().AddHours(-1), Now().AddHours(12), serial);
        using var withKey = issued.CopyWithPrivateKey(leafKey);

        // Round-trip through PKCS#12 so the key is persisted: Windows' TLS stack cannot use an
        // ephemeral in-memory key for a server handshake.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }

    public void Dispose()
    {
        Root.Dispose();
        _rootKey.Dispose();
    }

    private static DateTimeOffset Now() => SystemClock.Instance.GetCurrentInstant().ToDateTimeOffset();
}

/// <summary>An in-process Kestrel HTTPS server on a loopback port that answers 200 to every GET.</summary>
internal sealed class TlsStubServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TlsStubServer(WebApplication app, Uri address)
    {
        _app = app;
        Address = address;
    }

    internal Uri Address { get; }

    internal static async Task<TlsStubServer> StartAsync(X509Certificate2 serverCertificate)
    {
        ArgumentNullException.ThrowIfNull(serverCertificate);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost.UseUrls("https://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.ConfigureHttpsDefaults(https => https.ServerCertificate = serverCertificate));

        var app = builder.Build();
        app.MapGet("/", () => Results.Text("stub"));
        await app.StartAsync();

        var address = app.Urls.Single(url => url.StartsWith("https://", StringComparison.Ordinal));
        return new TlsStubServer(app, new Uri(address));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>The ways a configured trusted-root file can be wrong. Each must stop start-up.</summary>
internal static class BadTrustedRootFiles
{
    internal const string Missing = "missing";
    internal const string Empty = "empty";
    internal const string Malformed = "malformed";
    internal const string PrivateKey = "private-key";
    internal const string TwoCertificates = "two-certificates";
    internal const string NotACa = "not-a-ca";
    internal const string Directory = "directory";

    internal static readonly string[] All =
        [Missing, Empty, Malformed, PrivateKey, TwoCertificates, NotACa, Directory];

    /// <summary>Writes the bad file of the given kind under <paramref name="folder"/> and returns its path.</summary>
    internal static string Write(string kind, string folder)
    {
        var path = Path.Combine(folder, "root-" + kind + ".crt");
        using var pki = TlsTestPki.Create("decisya-test-root-a");

        switch (kind)
        {
            case Missing:
                break;
            case Empty:
                File.WriteAllText(path, string.Empty);
                break;
            case Malformed:
                // A well-framed PEM block whose body is not a certificate.
                File.WriteAllText(
                    path,
                    "-----BEGIN CERTIFICATE-----\n" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(120))
                    + "\n-----END CERTIFICATE-----\n");
                break;
            case PrivateKey:
                // Caddy's data volume holds root.key next to root.crt: mounting that file is the mistake.
                using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                {
                    File.WriteAllText(path, pki.RootPem + key.ExportPkcs8PrivateKeyPem());
                }

                break;
            case TwoCertificates:
                using (var second = TlsTestPki.Create("decisya-test-root-b"))
                {
                    File.WriteAllText(path, pki.RootPem + second.RootPem);
                }

                break;
            case NotACa:
                using (var leaf = pki.IssueServerCertificate(["localhost"]))
                {
                    File.WriteAllText(path, leaf.ExportCertificatePem());
                }

                break;
            case Directory:
                System.IO.Directory.CreateDirectory(path);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown bad-file kind.");
        }

        return path;
    }
}
