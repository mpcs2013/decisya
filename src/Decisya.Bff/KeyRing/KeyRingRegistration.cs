using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Decisya.Bff.KeyRing;

/// <summary>
/// The Data Protection set-up, moved out of <c>Program.cs</c> (#122, G2 D9 to D11). The ring lives on
/// the file system, never in Redis. Outside Development it is wrapped with
/// <c>ProtectKeysWithCertificate</c> (RSA, at least 3072 bits, from a file secret) and unwrapped with the
/// current and the previous certificate; the key lifetime is 90 days, set in code. Development without a
/// certificate keeps the framework's per-user default and runs no start-up check.
/// </summary>
internal static class KeyRingRegistration
{
    internal const string ApplicationName = "Decisya.Bff";

    internal static readonly TimeSpan KeyLifetime = TimeSpan.FromDays(90);

    internal static IServiceCollection AddBffDataProtection(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        // Validation runs at start-up in every environment (a bad certificate never silently falls back).
        services.AddSingleton<IValidateOptions<BffOptions>, KeyRingOptionsValidator>();

        var dataProtection = services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .SetDefaultKeyLifetime(KeyLifetime);

        var keyRingPath = configuration["Bff:DataProtection:KeyRingPath"];
        if (!string.IsNullOrWhiteSpace(keyRingPath))
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));
        }

        string? currentFingerprint = null;
        string? previousFingerprint = null;
        if (KeyRingCertificates.TryLoad(
                configuration["Bff:DataProtection:Certificate"], configuration["Bff:DataProtection:CertificatePassword"], out var current))
        {
            currentFingerprint = KeyRingCertificates.Fingerprint(current!);
            var unprotect = new List<X509Certificate2> { current! };
            if (KeyRingCertificates.TryLoad(
                    configuration["Bff:DataProtection:PreviousCertificate"],
                    configuration["Bff:DataProtection:PreviousCertificatePassword"],
                    out var previous))
            {
                previousFingerprint = KeyRingCertificates.Fingerprint(previous!);
                if (!string.Equals(previousFingerprint, currentFingerprint, StringComparison.Ordinal))
                {
                    unprotect.Add(previous!);
                }
                else
                {
                    previousFingerprint = null;
                    previous!.Dispose();
                }
            }

            dataProtection.ProtectKeysWithCertificate(current!);
            dataProtection.UnprotectKeysWithAnyCertificate([.. unprotect]);
        }

        var enabled = !environment.IsDevelopment() || currentFingerprint is not null;
        services.AddSingleton(new KeyRingCheckSettings(enabled, keyRingPath, currentFingerprint, previousFingerprint));
        services.AddSingleton<IHostedService, KeyRingStartupCheck>();

        return services;
    }

    internal static IServiceCollection AddBffDataProtection(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Services.AddBffDataProtection(builder.Configuration, builder.Environment);
    }
}
