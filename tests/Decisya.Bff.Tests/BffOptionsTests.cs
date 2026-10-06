using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Decisya.Bff.Tests;

/// <summary>
/// G4-18-04 (T-08, T-09): the three Development-only relaxations must fail startup in
/// Production, and the same configuration must start in Development. No Docker: options
/// validation runs before anything ever calls Keycloak or Redis, so this runs in the unit
/// lane.
/// </summary>
public class BffOptionsTests
{
    [Theory]
    [MemberData(nameof(InvalidOutsideDevelopmentCases))]
    public void A_Development_only_relaxation_fails_startup_in_Production(
        string overriddenKey, string? overriddenValue)
    {
        var overrides = TestConfiguration.GoodOverrides();

        // #120: outside Development AddServiceDefaults also requires a named AllowedHosts and a
        // valid UserIdHashKey (base64, at least 32 bytes). Supply both, so the host fails for the
        // relaxation under test and for no other reason.
        overrides["AllowedHosts"] = "localhost";
        overrides["Decisya:Observability:UserIdHashKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        overrides[overriddenKey] = overriddenValue;

        using var factory = new ConfigurableBffFactory("Production", overrides);

        var exception = Record.Exception(() => factory.Server);

        exception.Should().NotBeNull();
        var validationException = FindOptionsValidationException(exception!);
        validationException.Should().NotBeNull("startup should fail with an options validation failure, not some other error");

        // The message names the key, never the value (CLAUDE.md).
        // ("false" is skipped: it is also a word in the fixed message, "may be false only in
        // Development", so it proves nothing either way.)
        if (overriddenValue is not null && overriddenValue != "false")
        {
            validationException!.Message.Should().NotContain(overriddenValue);
        }
    }

    [Fact]
    public void The_same_configuration_that_fails_in_Production_starts_in_Development()
    {
        using var factory = new ConfigurableBffFactory("Development", TestConfiguration.GoodOverrides());

        var exception = Record.Exception(() => factory.Server);

        exception.Should().BeNull();
    }

    public static IEnumerable<object?[]> InvalidOutsideDevelopmentCases()
    {
        yield return new object?[] { "Bff:Oidc:RequireHttpsMetadata", "false" };
        yield return new object?[] { "Bff:Oidc:Authority", "http://example.test/realms/decisya" };
        yield return new object?[] { "Bff:DataProtection:KeyRingPath", null };
        // #19 G4-19-01 (T-02): the configured-string half of the https-only destination check.
        yield return new object?[] { "Bff:Api:Address", "http://decisya-api" };
        yield return new object?[] { "Bff:Api:Address", "decisya-api" };
    }

    private static OptionsValidationException? FindOptionsValidationException(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is OptionsValidationException optionsValidationException)
            {
                return optionsValidationException;
            }
        }

        return null;
    }

    private sealed class ConfigurableBffFactory(string environmentName, Dictionary<string, string?> overrides)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environmentName);
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
                configurationBuilder.AddInMemoryCollection(overrides));
        }
    }
}
