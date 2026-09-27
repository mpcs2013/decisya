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
        overrides[overriddenKey] = overriddenValue;

        using var factory = new ConfigurableBffFactory("Production", overrides);

        var exception = Record.Exception(() => factory.Server);

        exception.Should().NotBeNull();
        var validationException = FindOptionsValidationException(exception!);
        validationException.Should().NotBeNull("startup should fail with an options validation failure, not some other error");

        // The message names the key, never the value (CLAUDE.md).
        if (overriddenValue is not null)
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
