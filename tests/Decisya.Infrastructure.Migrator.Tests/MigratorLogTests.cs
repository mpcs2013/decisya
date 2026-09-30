using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Decisya.Infrastructure.Migrator.Tests;

/// <summary>
/// G3 G4-21-05: "It never logs a command text, a connection string or the password." /
/// "Log capture at Debug over a full migrator run: no record contains the password,
/// 'Password=', or the owner connection string." <see cref="MigratorLog"/> is the only logger
/// <c>Program.cs</c> calls; <see cref="MigrationRunner"/> itself attaches no
/// <c>ILoggerFactory</c> to its <c>DbContextOptionsBuilder</c> or its raw
/// <c>NpgsqlConnection</c>/<c>NpgsqlCommand</c> calls, so EF Core and Npgsql can structurally
/// never emit SQL command text through Microsoft.Extensions.Logging during a migrator run — the
/// log surface this test suite has to prove safe is exactly <see cref="MigratorLog"/>'s two
/// messages.
/// </summary>
public class MigratorLogTests
{
    [Trait("Category", "Unit")]
    [Fact]
    public void MigrationRunner_attaches_no_logger_factory_that_could_surface_SQL_command_text()
    {
        var migrationRunnerSource = File.ReadAllText(
            RepoPaths.Find(Path.Combine("src", "Decisya.Infrastructure.Migrator", "MigrationRunner.cs")));
        var optionsSource = File.ReadAllText(
            RepoPaths.Find(Path.Combine("src", "Modules", "Tenancy", "Decisya.Modules.Tenancy", "Infrastructure", "TenancyDbContextOptions.cs")));

        var entitlementsOptionsSource = File.ReadAllText(
            RepoPaths.Find(Path.Combine("src", "Modules", "Entitlements", "Decisya.Modules.Entitlements", "Infrastructure", "EntitlementsDbContextOptions.cs")));

        entitlementsOptionsSource.Should().NotContain("UseLoggerFactory");
        entitlementsOptionsSource.Should().NotContain("EnableSensitiveDataLogging");
        migrationRunnerSource.Should().NotContain("UseLoggerFactory");
        migrationRunnerSource.Should().NotContain("EnableSensitiveDataLogging");
        optionsSource.Should().NotContain("UseLoggerFactory");
        optionsSource.Should().NotContain("EnableSensitiveDataLogging");
    }

    [Trait("Category", "Unit")]
    [Fact]
    public void ConfigurationError_message_template_carries_only_the_already_safe_reason_text()
    {
        var method = typeof(MigratorLog).GetMethod("ConfigurationError", BindingFlags.NonPublic | BindingFlags.Static)!;
        var attribute = method.GetCustomAttribute<LoggerMessageAttribute>()!;

        attribute.Message.Should().Be("{Reason}");
        method.GetParameters().Select(p => p.Name).Should().Equal("logger", "reason");
    }

    [Trait("Category", "Unit")]
    [Fact]
    public void MigrationCompleted_message_template_carries_no_parameters_at_all()
    {
        var method = typeof(MigratorLog).GetMethod("MigrationCompleted", BindingFlags.NonPublic | BindingFlags.Static)!;

        // No parameter beyond the logger itself means this message can never carry a secret,
        // by construction — not just by the current literal template text.
        method.GetParameters().Select(p => p.Name).Should().Equal("logger");
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task A_failed_run_logs_only_the_configuration_key_name_never_the_password_or_connection_string()
    {
        var provider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(provider);
            builder.SetMinimumLevel(LogLevel.Trace);
        });
        var logger = loggerFactory.CreateLogger("Decisya.Infrastructure.Migrator");

        const string ownerConnectionString = "Host=db.invalid;Database=decisya;Username=postgres;Password=super-secret-owner-password";
        const string badPassword = "tooShort";

        try
        {
            await MigrationRunner.RunAsync(ownerConnectionString, badPassword, "AValidAlphaNumericPassword12345678", TestContext.Current.CancellationToken);
            throw new InvalidOperationException("Expected RunAsync to throw for an invalid password.");
        }
        catch (InvalidOperationException ex)
        {
            // Exactly Program.cs's own catch block (issue #21, G2).
            MigratorLog.ConfigurationError(logger, ex.Message);
        }

        var record = provider.Records.Should().ContainSingle().Which;
        record.Contains(badPassword).Should().BeFalse();
        record.Contains("super-secret-owner-password").Should().BeFalse();
        record.Contains(ownerConnectionString).Should().BeFalse();
        record.Contains("Password=").Should().BeFalse();
        record.Message.Should().Contain("Migrator:TenancyRolePassword");
    }
}
