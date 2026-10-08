using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;
using Decisya.Bff.KeyRing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Decisya.Bff.Tests.KeyRing;

/// <summary>
/// #122 G2 D10 and D12, G3 G4-122-02 rule 2, G4-122-03 c and S-122-07: the certificate validator, the strict
/// file detector on crafted files, and the generator mode's argument, stdin and terminal checks.
/// </summary>
[Trait("Category", "Unit")]
public class KeyRingValidationAndGeneratorTests
{
    private static BffOptions Options(TestCertificate? current, TestCertificate? previous = null, string? pfxOverride = null) => new()
    {
        DataProtection = new BffOptions.DataProtectionOptions
        {
            KeyRingPath = "/unused",
            Certificate = pfxOverride ?? current?.Pfx,
            CertificatePassword = current?.Password,
            PreviousCertificate = previous?.Pfx,
            PreviousCertificatePassword = previous?.Password,
        },
    };

    private static ValidateOptionsResult Validate(BffOptions options, string environment = "Production") =>
        new KeyRingOptionsValidator(new StubEnvironment(environment)).Validate(null, options);

    [Fact]
    public void A_valid_current_certificate_and_an_empty_previous_pair_pass()
    {
        Validate(Options(TestCertificates.A)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void A_valid_rotation_pair_passes()
    {
        Validate(Options(TestCertificates.B, TestCertificates.A)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void A_missing_certificate_fails_outside_Development_and_passes_in_Development()
    {
        var result = Validate(Options(null));

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Bff:DataProtection:Certificate").And.Contain("Bff:DataProtection:CertificatePassword");
        Validate(Options(null), "Development").Succeeded.Should().BeTrue();
    }

    [Fact]
    public void A_weak_certificate_fails_in_every_environment_and_the_message_leaks_nothing()
    {
        var weak = TestCertificates.Weak;
        foreach (var environment in new[] { "Production", "Development" })
        {
            var result = Validate(Options(weak), environment);

            result.Failed.Should().BeTrue();
            result.FailureMessage.Should().Contain("Bff:DataProtection:Certificate").And.Contain("3072");
            AssertNoSubstring(result.FailureMessage!, weak.Pfx);
            AssertNoSubstring(result.FailureMessage!, weak.Password);
        }
    }

    [Theory]
    [InlineData("not base64 at all!!")]
    [InlineData("AAAA")]
    public void Garbage_fails_naming_the_key_only(string garbage)
    {
        var result = Validate(Options(TestCertificates.A, pfxOverride: garbage));

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Bff:DataProtection:Certificate").And.NotContain(garbage);
    }

    [Fact]
    public void A_wrong_password_fails_without_echoing_either_value()
    {
        var options = new BffOptions
        {
            DataProtection = new BffOptions.DataProtectionOptions
            {
                KeyRingPath = "/unused",
                Certificate = TestCertificates.A.Pfx,
                CertificatePassword = "wrong-" + TestCertificates.A.Password,
            },
        };

        var result = Validate(options);

        result.Failed.Should().BeTrue();
        AssertNoSubstring(result.FailureMessage!, TestCertificates.A.Pfx);
        AssertNoSubstring(result.FailureMessage!, TestCertificates.A.Password);
    }

    [Fact]
    public void The_previous_pair_must_be_complete_distinct_and_valid()
    {
        var half = new BffOptions
        {
            DataProtection = new BffOptions.DataProtectionOptions
            {
                KeyRingPath = "/unused",
                Certificate = TestCertificates.A.Pfx,
                CertificatePassword = TestCertificates.A.Password,
                PreviousCertificate = TestCertificates.B.Pfx,
            },
        };

        Validate(half).FailureMessage.Should().Contain("PreviousCertificatePassword");
        Validate(Options(TestCertificates.A, TestCertificates.A)).FailureMessage.Should().Contain("must differ");
    }

    private static void AssertNoSubstring(string message, string secret)
    {
        for (var i = 0; i + 16 <= secret.Length; i += 3)
        {
            message.Should().NotContain(secret.Substring(i, 16));
        }
    }

    [Fact]
    public void The_detector_accepts_a_revocation_file_and_rejects_unreadable_or_foreign_files()
    {
        KeyRingFileRules.Inspect(XDocument.Parse("<revocation version=\"1\"><revocationDate>2026-01-01T00:00:00Z</revocationDate></revocation>"))
            .Verdict.Should().Be(KeyFileVerdict.Ok);
        KeyRingFileRules.Inspect(XDocument.Parse("<other/>")).Verdict.Should().Be(KeyFileVerdict.NotCertificateWrapped);

        var directory = KeyRingHosts.NewKeyDirectory();
        var broken = Path.Combine(directory, "key-broken.xml");
        File.WriteAllText(broken, "<key><unclosed>");
        KeyRingFileRules.Inspect(broken).Verdict.Should().Be(KeyFileVerdict.Unreadable);
        File.WriteAllText(broken, "<!DOCTYPE key [<!ENTITY x \"y\">]><key>&x;</key>");
        KeyRingFileRules.Inspect(broken).Verdict.Should().Be(KeyFileVerdict.Unreadable, "DTDs are prohibited");
    }

    [Fact]
    public void The_detector_needs_both_the_certificate_decryptor_and_an_EncryptedData_element()
    {
        const string Decryptor = KeyRingFileRules.CertificateDecryptorType;
        KeyFileVerdict Verdict(string inner, string decryptor = Decryptor) => KeyRingFileRules.Inspect(XDocument.Parse(
            $"<key id=\"x\"><descriptor><encryptedSecret decryptorType=\"{decryptor}, Microsoft.AspNetCore.DataProtection\">{inner}</encryptedSecret></descriptor></key>")).Verdict;

        Verdict("<EncryptedData/>").Should().Be(KeyFileVerdict.Ok);
        Verdict("<value/>").Should().Be(KeyFileVerdict.NotCertificateWrapped);
        Verdict("<EncryptedData/>", "Microsoft.AspNetCore.DataProtection.XmlEncryption.NullXmlDecryptor").Should().Be(KeyFileVerdict.NotCertificateWrapped);
        Verdict("<EncryptedData/><masterKey/>").Should().Be(KeyFileVerdict.PlaintextMaterial);
        Verdict("<EncryptedData><unencryptedKey/></EncryptedData>").Should().Be(KeyFileVerdict.PlaintextMaterial);
    }

    [Fact]
    public void The_generator_refuses_any_argument_list_but_the_exact_flag()
    {
        using var output = new MemoryStream();
        var password = new string('a', 40);

        string[][] cases =
        [
            [KeyRingCertificateGenerator.Argument, "--urls=http://127.0.0.1:80"],
            ["--other", KeyRingCertificateGenerator.Argument],
            [KeyRingCertificateGenerator.Argument + "=1"],
            [KeyRingCertificateGenerator.Argument.ToUpperInvariant()],
        ];
        foreach (var args in cases)
        {
            KeyRingCertificateGenerator.Mentions(args).Should().BeTrue();
            using var stderr = new StringWriter();
            KeyRingCertificateGenerator.Run(args, new StringReader(password), output, stdoutIsRedirected: true, stderr)
                .Should().Be(KeyRingCertificateGenerator.ExitRefused);
        }

        output.Length.Should().Be(0);
        KeyRingCertificateGenerator.Mentions(["--health-probe"]).Should().BeFalse();
    }

    [Fact]
    public void The_generator_refuses_a_terminal_stdout_and_writes_nothing()
    {
        using var output = new MemoryStream();
        using var stderr = new StringWriter();
        var password = new string('b', 40);

        KeyRingCertificateGenerator.Run([KeyRingCertificateGenerator.Argument], new StringReader(password), output, stdoutIsRedirected: false, stderr)
            .Should().Be(KeyRingCertificateGenerator.ExitRefused);

        output.Length.Should().Be(0);
        stderr.ToString().Should().NotContain(password);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("short1234567890")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789 with spaces")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789-_")]
    public void The_generator_refuses_an_empty_or_wrongly_shaped_password(string stdin)
    {
        using var output = new MemoryStream();
        using var stderr = new StringWriter();

        KeyRingCertificateGenerator.Run([KeyRingCertificateGenerator.Argument], new StringReader(stdin), output, stdoutIsRedirected: true, stderr)
            .Should().Be(KeyRingCertificateGenerator.ExitFailed);

        output.Length.Should().Be(0, "nothing is generated for a bad password");
        stderr.ToString().Should().NotContain(stdin.Trim().Length > 5 ? stdin.Trim() : "\u0001");
    }

    [Fact]
    public void The_generator_refuses_a_password_that_is_too_long()
    {
        using var output = new MemoryStream();
        using var stderr = new StringWriter();

        KeyRingCertificateGenerator.Run(
            [KeyRingCertificateGenerator.Argument],
            new StringReader(new string('a', KeyRingCertificateGenerator.MaximumPasswordLength + 1)),
            output,
            stdoutIsRedirected: true,
            stderr).Should().Be(KeyRingCertificateGenerator.ExitFailed);
        output.Length.Should().Be(0);
    }

    [Fact]
    public void The_generator_writes_a_base64_RSA_4096_pkcs12_that_the_validator_accepts_and_nothing_else()
    {
        var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        using var output = new MemoryStream();
        using var stderr = new StringWriter();

        var exit = KeyRingCertificateGenerator.Run(
            [KeyRingCertificateGenerator.Argument], new StringReader(password + "\r\n"), output, stdoutIsRedirected: true, stderr);

        exit.Should().Be(KeyRingCertificateGenerator.ExitOk);
        stderr.ToString().Should().BeEmpty();
        var pfx = Encoding.ASCII.GetString(output.ToArray());
        pfx.Should().NotContain(password).And.NotContain("\n");

        var loaded = X509CertificateLoader.LoadPkcs12(
            Convert.FromBase64String(pfx),
            password,
            X509KeyStorageFlags.EphemeralKeySet);
        loaded.HasPrivateKey.Should().BeTrue();
        loaded.GetRSAPublicKey()!.KeySize.Should().Be(4096);
        loaded.Subject.Should().Be("CN=Decisya BFF Data Protection");

        var options = new BffOptions
        {
            DataProtection = new BffOptions.DataProtectionOptions
            {
                KeyRingPath = "/unused",
                Certificate = pfx,
                CertificatePassword = password,
            },
        };
        Validate(options).Succeeded.Should().BeTrue();
    }

    private sealed class StubEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Decisya.Bff";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
