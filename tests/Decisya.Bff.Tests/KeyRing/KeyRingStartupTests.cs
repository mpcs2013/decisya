using System.Xml.Linq;
using Decisya.Bff.KeyRing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using static Decisya.Bff.Tests.KeyRing.KeyRingHosts;

namespace Decisya.Bff.Tests.KeyRing;

/// <summary>
/// #122 Stories 8 to 11, NFR-54 to NFR-56 and G3 G4-122-02: the real <c>AddBffDataProtection</c> and
/// fail-closed start-up check over temporary key directories. Every refusal also proves that no key file
/// was written. No Docker; the Unix mode checks run only off Windows (CI and the stack).
/// </summary>
[Trait("Category", "Unit")]
public class KeyRingStartupTests
{
    private static readonly byte[] Payload = [1, 2, 3, 4, 5, 6, 7, 8];

    private static async Task<InvalidOperationException> StartFailsAsync(IHost host)
    {
        var failure = await Record.ExceptionAsync(() => host.StartAsync(TestContext.Current.CancellationToken));
        failure.Should().BeOfType<InvalidOperationException>();
        failure!.Message.Should().Be(KeyRingStartupCheck.GenericFailureMessage);
        return (InvalidOperationException)failure;
    }

    /// <summary>Creates a ring with the given configuration and returns the cipher text of <see cref="Payload"/>.</summary>
    private static async Task<byte[]> CreateRingAsync(string directory, TestCertificate? current)
    {
        using var host = Build(directory, current);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var cipher = Protect(host, Payload);
        await host.StopAsync(TestContext.Current.CancellationToken);
        return cipher;
    }

    [Fact]
    public async Task An_empty_directory_is_a_normal_first_start_with_one_certificate_wrapped_key()
    {
        var directory = NewKeyDirectory(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var host = Build(directory, TestCertificates.A);

        await host.StartAsync(TestContext.Current.CancellationToken);
        var cipher = Protect(host, Payload);

        var files = KeyFiles(directory);
        files.Should().ContainSingle();
        KeyRingFileRules.Inspect(files[0]).Verdict.Should().Be(KeyFileVerdict.Ok);
        var text = File.ReadAllText(files[0]);
        text.Should().Contain("encryptedSecret").And.Contain("EncryptedData").And.NotContain("masterKey");
        Unprotect(host, cipher).Should().Equal(Payload);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_restart_with_the_same_certificate_still_unprotects()
    {
        var directory = NewKeyDirectory();
        var cipher = await CreateRingAsync(directory, TestCertificates.A);

        using var second = Build(directory, TestCertificates.A);
        await second.StartAsync(TestContext.Current.CancellationToken);

        Unprotect(second, cipher).Should().Equal(Payload);
        KeyFiles(directory).Should().ContainSingle("a restart creates no second key");
    }

    [Fact]
    public async Task The_default_key_lifetime_is_90_days_set_in_code()
    {
        using var host = Build(NewKeyDirectory(), TestCertificates.A);

        host.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.NewKeyLifetime.Should().Be(TimeSpan.FromDays(90));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task An_existing_ring_without_the_certificate_refuses_to_start_and_writes_no_key()
    {
        var directory = NewKeyDirectory();
        await CreateRingAsync(directory, TestCertificates.A);
        var before = KeyFiles(directory);
        var logs = new CapturingLoggerProvider();

        using var host = Build(directory, TestCertificates.B, logs: logs);
        await StartFailsAsync(host);

        KeyFiles(directory).Should().BeEquivalentTo(before, "no silent new ring");
        var failure = logs.Records.Single(r => r.EventId == 1830);
        failure.StateText.Should().Contain("Rule=key_undecryptable");
        logs.Records.Should().NotContain(r => r.Contains(TestCertificates.A.Password) || r.Contains(TestCertificates.B.Password));
    }

    [Fact]
    public async Task A_start_up_failure_shows_only_the_generic_message_and_the_full_detail_log_line_carries_the_trace_id()
    {
        var directory = NewKeyDirectory();
        await CreateRingAsync(directory, TestCertificates.A);
        var logs = new CapturingLoggerProvider();
        using var source = new System.Diagnostics.ActivitySource("Decisya.Bff.Tests.KeyRing");
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = s => s.Name == source.Name,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        using var activity = source.StartActivity("keyring-startup");
        activity.Should().NotBeNull();

        using var host = Build(directory, TestCertificates.B, logs: logs);
        var failure = await StartFailsAsync(host);

        failure.Message.Should().Be(KeyRingStartupCheck.GenericFailureMessage);
        failure.Message.Should().NotContain("key_undecryptable").And.NotContain(directory).And.NotContain("KeyId");
        failure.InnerException.Should().BeNull("no inner exception detail travels with the failure");
        var record = logs.Records.Single(r => r.EventId == 1830);
        record.Level.Should().Be(Microsoft.Extensions.Logging.LogLevel.Error);
        record.StateText.Should().Contain("Rule=key_undecryptable").And.Contain("KeyId=");
        record.TraceId.Should().Be(activity!.TraceId.ToHexString(), "the structured line is correlated to the start-up trace");
        record.Contains(directory).Should().BeFalse("the log line names the rule and the key id, never the path");
    }

    [Fact]
    public async Task The_failure_line_carries_a_trace_id_from_the_check_own_activity_when_the_caller_has_none()
    {
        var directory = NewKeyDirectory();
        await CreateRingAsync(directory, TestCertificates.A);
        var logs = new CapturingLoggerProvider();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = s => s.Name == "Decisya.Bff",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        System.Diagnostics.Activity.Current = null;

        using var host = Build(directory, TestCertificates.B, logs: logs);
        await StartFailsAsync(host);

        var record = logs.Records.Single(r => r.EventId == 1830);
        record.TraceId.Should().NotBeNullOrEmpty();
        record.TraceId.Should().NotBe(new System.Diagnostics.ActivityTraceId().ToHexString());
    }

    [Fact]
    public async Task Rotation_keeps_every_earlier_payload_readable_and_reports_the_previous_only_expiry()
    {
        var directory = NewKeyDirectory();
        var cipher = await CreateRingAsync(directory, TestCertificates.A);
        var logs = new CapturingLoggerProvider();

        using var rotated = Build(directory, TestCertificates.B, previous: TestCertificates.A, logs: logs);
        await rotated.StartAsync(TestContext.Current.CancellationToken);

        Unprotect(rotated, cipher).Should().Equal(Payload);
        var info = logs.Records.Single(r => r.EventId == 1831);
        info.StateText.Should().NotContain("none", "the live key is wrapped by the previous certificate only");
        await rotated.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Retiring_the_previous_certificate_while_a_key_it_wraps_is_live_fails_closed()
    {
        var directory = NewKeyDirectory();
        await CreateRingAsync(directory, TestCertificates.A);
        var before = KeyFiles(directory);

        using var retired = Build(directory, TestCertificates.B);
        await StartFailsAsync(retired);

        KeyFiles(directory).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task A_plaintext_ring_from_the_previous_stack_refuses_to_start()
    {
        var directory = NewKeyDirectory();
        CreatePlaintextRing(directory);
        var before = KeyFiles(directory);
        var logs = new CapturingLoggerProvider();

        using var host = Build(directory, TestCertificates.A, logs: logs);
        await StartFailsAsync(host);

        KeyFiles(directory).Should().BeEquivalentTo(before);
        logs.Records.Single(r => r.EventId == 1830).StateText.Should().Contain("Rule=key_file_plaintext");
    }

    [Fact]
    public async Task A_key_written_through_NullXmlEncryptor_is_plaintext_even_though_it_has_an_encryptedSecret()
    {
        var directory = NewKeyDirectory();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().SetApplicationName("Decisya.Bff").PersistKeysToFileSystem(new DirectoryInfo(directory));
        services.Configure<KeyManagementOptions>(options => options.XmlEncryptor = new NullXmlEncryptor());
        using (var provider = services.BuildServiceProvider())
        {
            provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("x").Protect(Payload);
        }

        var file = KeyFiles(directory).Single();
        File.ReadAllText(file).Should().Contain("encryptedSecret").And.Contain("masterKey");
        KeyRingFileRules.Inspect(file).Verdict.Should().Be(KeyFileVerdict.PlaintextMaterial);

        using var host = Build(directory, TestCertificates.A);
        await StartFailsAsync(host);
        KeyFiles(directory).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_key_with_a_foreign_decryptor_or_a_stray_masterKey_or_an_expired_plaintext_key_refuses()
    {
        foreach (var tamper in new Action<string>[] { RewriteDecryptor, AddStrayMasterKey, ReplaceWithExpiredPlaintextKey })
        {
            var directory = NewKeyDirectory();
            await CreateRingAsync(directory, TestCertificates.A);
            var file = KeyFiles(directory).Single();
            tamper(file);
            var before = KeyFiles(directory);

            using var host = Build(directory, TestCertificates.A);
            await StartFailsAsync(host);

            KeyFiles(directory).Should().BeEquivalentTo(before, "the check wrote nothing");
        }
    }

    [Fact]
    public async Task A_missing_directory_fails_closed()
    {
        using var host = Build(Path.Combine(Path.GetTempPath(), "decisya-keyring-missing-" + Guid.NewGuid().ToString("N")), TestCertificates.A);

        await StartFailsAsync(host);
    }

    [Fact]
    public async Task A_directory_open_to_group_or_other_fails_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix modes exist only on Linux (CI and the stack).
        }

        var directory = NewKeyDirectory(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        using var host = Build(directory, TestCertificates.A);

        await StartFailsAsync(host);
        KeyFiles(directory).Should().BeEmpty();
    }

    [Fact]
    public async Task Development_without_a_certificate_runs_no_check_and_keeps_the_unwrapped_local_ring()
    {
        var directory = NewKeyDirectory();
        using var host = Build(directory, current: null, environment: "Development");

        await host.StartAsync(TestContext.Current.CancellationToken);
        Protect(host, Payload);

        host.Services.GetRequiredService<KeyRingCheckSettings>().Enabled.Should().BeFalse();
        File.ReadAllText(KeyFiles(directory).Single()).Should().Contain("masterKey");
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_check_runs_outside_Development_even_without_a_certificate()
    {
        using var host = Build(NewKeyDirectory(), current: null);

        host.Services.GetRequiredService<KeyRingCheckSettings>().Enabled.Should().BeTrue();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_copy_of_the_ring_without_the_certificate_cannot_be_read()
    {
        var directory = NewKeyDirectory();
        await CreateRingAsync(directory, TestCertificates.A);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().SetApplicationName("Decisya.Bff").PersistKeysToFileSystem(new DirectoryInfo(directory));
        using var provider = services.BuildServiceProvider();

        var keys = provider.GetRequiredService<IKeyManager>().GetAllKeys();

        keys.Should().NotBeEmpty();
        Record.Exception(() => _ = keys.First().Descriptor).Should().NotBeNull();
    }

    private static void CreatePlaintextRing(string directory)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().SetApplicationName("Decisya.Bff").PersistKeysToFileSystem(new DirectoryInfo(directory));
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("x").Protect(Payload);
    }

    private static void RewriteDecryptor(string file)
    {
        var text = File.ReadAllText(file);
        File.WriteAllText(
            file,
            text.Replace("Microsoft.AspNetCore.DataProtection.XmlEncryption.EncryptedXmlDecryptor", "Microsoft.AspNetCore.DataProtection.XmlEncryption.DpapiXmlDecryptor", StringComparison.Ordinal));
    }

    private static void AddStrayMasterKey(string file)
    {
        var document = XDocument.Load(file);
        document.Root!.Add(new XElement("masterKey", "c3RyYXk="));
        document.Save(file);
    }

    private static void ReplaceWithExpiredPlaintextKey(string file)
    {
        var plain = NewKeyDirectory();
        CreatePlaintextRing(plain);
        var document = XDocument.Load(KeyFiles(plain).Single());
        document.Root!.Element("expirationDate")!.Value = "2020-01-01T00:00:00Z";
        document.Save(file);
    }
}
