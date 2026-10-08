using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NodaTime;

namespace Decisya.Bff.KeyRing;

/// <summary>
/// The <c>--generate-keyring-certificate</c> mode (#122, G2 D12, G3 G4-122-03 c). Handled before the host
/// is built, so no configuration, logging, telemetry or hosted service exists. It is entered only when
/// the argument list is exactly that one flag; any other list that names the flag exits 2. The password
/// comes on stdin (one line, at least 32 letters and digits), stdout must not be a terminal, and the
/// base64 PKCS#12 (RSA 4096, PBES2 AES-256) goes to stdout only. Every message is fixed text: no exception
/// message, no value. No network is used.
/// </summary>
internal static class KeyRingCertificateGenerator
{
    internal const string Argument = "--generate-keyring-certificate";
    internal const int ExitOk = 0;
    internal const int ExitFailed = 1;
    internal const int ExitRefused = 2;
    internal const int MinimumPasswordLength = 32;
    internal const int MaximumPasswordLength = 256;

    /// <summary>True when any argument names the flag (so a non-exact use is refused, never ignored).</summary>
    internal static bool Mentions(string[] args) =>
        args.Any(arg => arg.StartsWith(Argument, StringComparison.OrdinalIgnoreCase));

    /// <summary>Call first in <c>Program.cs</c>. Returns when the mode was not requested; otherwise ends the process.</summary>
    internal static void ExitIfRequested(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (!Mentions(args))
        {
            return;
        }

        using var stdout = Console.OpenStandardOutput();
        Environment.Exit(Run(args, Console.In, stdout, Console.IsOutputRedirected, Console.Error));
    }

    internal static int Run(string[] args, TextReader stdin, Stream stdout, bool stdoutIsRedirected, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (args.Length != 1 || !string.Equals(args[0], Argument, StringComparison.Ordinal))
        {
            stderr.WriteLine("Refused: this mode takes no other argument.");
            return ExitRefused;
        }

        if (!stdoutIsRedirected)
        {
            stderr.WriteLine("Refused: stdout is a terminal; redirect it to a pipe or a file.");
            return ExitRefused;
        }

        var password = ReadPassword(stdin);
        if (password is null)
        {
            stderr.WriteLine("Failed: the password on stdin is missing or has the wrong shape.");
            return ExitFailed;
        }

        try
        {
            var pfx = Generate(password);
            stdout.Write(Encoding.ASCII.GetBytes(pfx));
            stdout.Flush();
            return ExitOk;
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or InvalidOperationException)
        {
            stderr.WriteLine("Failed: the certificate could not be generated.");
            return ExitFailed;
        }
    }

    /// <summary>The base64 PKCS#12 of a new self-signed RSA 4096 certificate, valid 25 months.</summary>
    internal static string Generate(string password)
    {
        using var rsa = RSA.Create(4096);
        var request = new CertificateRequest(
            "CN=Decisya BFF Data Protection", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = SystemClock.Instance.GetCurrentInstant().ToDateTimeOffset();
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddMonths(25));
        return Convert.ToBase64String(certificate.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, password));
    }

    /// <summary>Reads one bounded line; returns null unless it is 32 to 256 ASCII letters and digits.</summary>
    internal static string? ReadPassword(TextReader stdin)
    {
        var builder = new StringBuilder();
        int next;
        while ((next = stdin.Read()) >= 0)
        {
            var c = (char)next;
            if (c == '\n')
            {
                break;
            }

            if (c == '\r')
            {
                continue;
            }

            if (!char.IsAsciiLetterOrDigit(c) || builder.Length >= MaximumPasswordLength)
            {
                return null;
            }

            builder.Append(c);
        }

        return builder.Length >= MinimumPasswordLength ? builder.ToString() : null;
    }
}
