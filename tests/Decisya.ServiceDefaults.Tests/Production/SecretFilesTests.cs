using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Decisya.ServiceDefaults.Tests.Production;

/// <summary>Issue #120, D6 and D10 item 11: the key-per-file source over the secrets directory.</summary>
public sealed class SecretFilesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "decisya-secrets-" + Guid.NewGuid().ToString("N"));

    public SecretFilesTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void A_double_underscore_file_name_becomes_a_configuration_key_and_the_newline_is_dropped()
    {
        var value = Canaries.Unique("conn");
        // The framework source trims one platform newline (Environment.NewLine), so the test
        // writes that one: "\n" on the Linux containers that run this, "\r\n" on a Windows host.
        File.WriteAllText(Path.Combine(_directory, "ConnectionStrings__tenancy"), value + Environment.NewLine);
        File.WriteAllText(Path.Combine(_directory, "Decisya__Plain"), "plain");

        var builder = NewBuilder(Environments.Production);
        builder.AddSecretFiles(_directory);

        builder.Configuration["ConnectionStrings:tenancy"].Should().Be(value);
        builder.Configuration["Decisya:Plain"].Should().Be("plain");
    }

    [Fact]
    public void A_secret_file_wins_over_an_earlier_configuration_source()
    {
        var value = Canaries.Unique("override");
        File.WriteAllText(Path.Combine(_directory, "Decisya__Key"), value);

        var builder = NewBuilder(Environments.Production);
        builder.Configuration.AddInMemoryCollection([new("Decisya:Key", "from-memory")]);
        builder.AddSecretFiles(_directory);

        builder.Configuration["Decisya:Key"].Should().Be(value);
    }

    [Fact]
    public void The_source_is_not_added_in_Development()
    {
        File.WriteAllText(Path.Combine(_directory, "Decisya__Key"), Canaries.Unique("dev"));

        var builder = NewBuilder(Environments.Development);
        var sourcesBefore = ((IConfigurationBuilder)builder.Configuration).Sources.Count;
        builder.AddSecretFiles(_directory);

        builder.Configuration["Decisya:Key"].Should().BeNull();
        ((IConfigurationBuilder)builder.Configuration).Sources.Should().HaveCount(sourcesBefore);
    }

    [Fact]
    public void A_missing_directory_is_not_an_error()
    {
        var builder = NewBuilder(Environments.Production);

        var act = () => builder.AddSecretFiles(Path.Combine(_directory, "does-not-exist"));

        act.Should().NotThrow();
    }

    [Fact]
    public void The_default_directory_is_run_secrets()
    {
        ProductionExtensions.DefaultSecretsDirectory.Should().Be("/run/secrets");
    }

    private static HostApplicationBuilder NewBuilder(string environment) =>
        Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environment,
            DisableDefaults = true,
        });
}
