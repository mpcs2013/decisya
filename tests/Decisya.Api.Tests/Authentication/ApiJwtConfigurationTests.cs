using System.Text.Json;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// #164 Story 3 / NFR-60 (fail-closed start-up). The audience and the token-rejected event have no
/// default in <c>Ostium.Authentication.Jwt</c>; a host without a usable value must not start. Every
/// start-up row goes through Decisya's own <c>AddDecisyaApiAuthentication</c> with the direct-start
/// pattern (<see cref="ApiStartHarness"/>), and the failure names the setting key, never its value.
/// </summary>
[Trait("Category", "Unit")]
public class ApiJwtConfigurationTests
{
    [Fact]
    public async Task A_complete_configuration_starts_the_host()
    {
        var failures = await ApiStartHarness.StartAndCollectFailuresAsync(ApiStartHarness.ValidConfiguration());

        failures.Should().BeEmpty("the failure rows below are only meaningful if the baseline starts");
    }

    // Scenario: The shipped configuration carries the required values
    [Fact]
    public void The_shipped_configuration_carries_the_required_values()
    {
        var path = RepoPaths.Find(Path.Combine("src", "Decisya.Api", "appsettings.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var jwt = document.RootElement.GetProperty("Api").GetProperty("Jwt");
        var tokenRejected = jwt.GetProperty("Events").GetProperty("TokenRejected");

        jwt.GetProperty("Audience").GetString().Should().Be("decisya-api");
        tokenRejected.GetProperty("Name").GetString().Should().Be("auth.token.rejected");
        tokenRejected.GetProperty("Id").ValueKind.Should().Be(JsonValueKind.Number);
        tokenRejected.GetProperty("Id").GetInt32().Should().Be(2003);
        jwt.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["Audience", "Events"],
            "Authority and TrustedRootPath come from the environment only (LaunchProfileAndConfigurationTests)");
    }

    [Fact]
    public void The_test_audience_constant_equals_the_shipped_audience()
    {
        var path = RepoPaths.Find(Path.Combine("src", "Decisya.Api", "appsettings.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        DecisyaTestTokens.Audience.Should().Be(
            document.RootElement.GetProperty("Api").GetProperty("Jwt").GetProperty("Audience").GetString());
    }

    // Scenario Outline: A bad audience stops start-up
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" decisya-api")]
    [InlineData("decisya-api ")]
    [InlineData("*")]
    public async Task A_bad_audience_stops_start_up_and_the_message_names_the_key_only(string? audience)
    {
        var configuration = ApiStartHarness.ValidConfiguration();
        if (audience is null)
        {
            configuration.Remove(ApiStartHarness.AudienceKey);
        }
        else
        {
            configuration[ApiStartHarness.AudienceKey] = audience;
        }

        var failures = await ApiStartHarness.StartAndCollectFailuresAsync(configuration);

        failures.Should().NotBeEmpty("start-up must fail before any request is served");
        var message = string.Join(" | ", failures);
        message.Should().Contain(ApiStartHarness.AudienceKey);
        if (audience is { Length: > 0 } && audience.Trim().Length > 0 && audience != "*")
        {
            message.Should().NotContain(audience, "the message never echoes the configured value");
        }
    }

    // Scenario Outline: A missing event setting stops start-up
    [Theory]
    [InlineData(ApiStartHarness.EventNameKey, null)]
    [InlineData(ApiStartHarness.EventNameKey, "")]
    [InlineData(ApiStartHarness.EventIdKey, null)]
    [InlineData(ApiStartHarness.EventIdKey, "")]
    public async Task A_missing_or_empty_event_setting_stops_start_up(string key, string? value)
    {
        var configuration = ApiStartHarness.ValidConfiguration();
        if (value is null)
        {
            configuration.Remove(key);
        }
        else
        {
            configuration[key] = value;
        }

        var failures = await ApiStartHarness.StartAndCollectFailuresAsync(configuration);

        failures.Should().NotBeEmpty("start-up must fail before any request is served");
        string.Join(" | ", failures).Should().Contain(key);
    }

    [Theory]
    [InlineData(ApiStartHarness.EventIdKey, "0")]
    [InlineData(ApiStartHarness.EventIdKey, "-1")]
    [InlineData(ApiStartHarness.EventNameKey, "has space")]
    public async Task An_unusable_event_setting_stops_start_up(string key, string value)
    {
        var configuration = ApiStartHarness.ValidConfiguration();
        configuration[key] = value;

        var failures = await ApiStartHarness.StartAndCollectFailuresAsync(configuration);

        failures.Should().NotBeEmpty();
        string.Join(" | ", failures).Should().Contain(key);
    }
}
