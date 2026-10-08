using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Decisya.Bff.KeyRing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Decisya.Bff.Tests.KeyRing;

/// <summary>A generated certificate as the stack stores it: base64 PKCS#12 plus its password. Random per run.</summary>
internal sealed record TestCertificate(
    string Pfx,
    X509Certificate2 Certificate,
    string Password);

internal static class TestCertificates
{
    private static readonly Lazy<TestCertificate> LazyA = new(() => Make("A", 3072));
    private static readonly Lazy<TestCertificate> LazyB = new(() => Make("B", 3072));
    private static readonly Lazy<TestCertificate> LazyWeak = new(() => Make("weak", 2048));

    internal static TestCertificate A => LazyA.Value;

    internal static TestCertificate B => LazyB.Value;

    internal static TestCertificate Weak => LazyWeak.Value;

    internal static TestCertificate Make(string name, int bits)
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        using var rsa = RSA.Create(bits);
        var request = new CertificateRequest("CN=Decisya test " + name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = SystemClock.Instance.GetCurrentInstant().ToDateTimeOffset();
        using var self = request.CreateSelfSigned(now.AddDays(-1), now.AddMonths(25));
        var pfx = Convert.ToBase64String(self.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, password));
        var loaded = X509CertificateLoader.LoadPkcs12(
            Convert.FromBase64String(pfx),
            password,
            X509KeyStorageFlags.EphemeralKeySet);
        return new TestCertificate(
            pfx,
            loaded,
            password);
    }
}

/// <summary>Mini hosts that run the real <c>AddBffDataProtection</c> and start-up check, without the rest of the BFF.</summary>
internal static class KeyRingHosts
{
    internal static string NewKeyDirectory(UnixFileMode? mode = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "decisya-keyring-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        if (mode is not null && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode.Value);
        }

        return path;
    }

    internal static IHost Build(
        string? keyRingPath,
        TestCertificate? current,
        TestCertificate? previous = null,
        string environment = "Production",
        CapturingLoggerProvider? logs = null,
        IClock? clock = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment });
        builder.Logging.ClearProviders();
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
            builder.Logging.SetMinimumLevel(LogLevel.Debug);
        }

        var values = new Dictionary<string, string?>();
        if (keyRingPath is not null)
        {
            values["Bff:DataProtection:KeyRingPath"] = keyRingPath;
        }

        if (current is not null)
        {
            values["Bff:DataProtection:Certificate"] = current.Pfx;
            values["Bff:DataProtection:CertificatePassword"] = current.Password;
        }

        if (previous is not null)
        {
            values["Bff:DataProtection:PreviousCertificate"] = previous.Pfx;
            values["Bff:DataProtection:PreviousCertificatePassword"] = previous.Password;
        }

        builder.Configuration.AddInMemoryCollection(values);
        builder.Services.AddSingleton<IClock>(clock ?? SystemClock.Instance);
        builder.Services.AddBffDataProtection(builder.Configuration, builder.Environment);
        return builder.Build();
    }

    internal static byte[] Protect(IHost host, byte[] payload) =>
        host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("keyring-test").Protect(payload);

    internal static byte[] Unprotect(IHost host, byte[] cipher) =>
        host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("keyring-test").Unprotect(cipher);

    internal static string[] KeyFiles(string directory) => Directory.GetFiles(directory, "*.xml", SearchOption.TopDirectoryOnly);
}
