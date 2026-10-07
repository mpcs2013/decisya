using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Decisya.ServiceDefaults.Tests.Production;

/// <summary>Issue #120, D6: <c>AllowedHosts</c> is validated outside Development, at start-up.</summary>
[Collection(RealAspNetCoreHostCollectionDefinition.Name)]
public class AllowedHostsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    [InlineData("a.test;*")]
    [InlineData("*.example.test")]
    public async Task A_missing_or_wildcard_AllowedHosts_fails_start_up_outside_Development(string? allowedHosts)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var act = async () =>
        {
            await using var host = await ProductionTestHost.StartAsync(
                Environments.Production,
                new Dictionary<string, string?> { ["AllowedHosts"] = allowedHosts },
                cancellationToken: cancellationToken);
        };

        (await act.Should().ThrowAsync<OptionsValidationException>())
            .Which.Message.Should().Contain("AllowedHosts");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("*")]
    [InlineData("a.test;*")]
    public async Task The_same_values_pass_in_Development(string? allowedHosts)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var host = await ProductionTestHost.StartAsync(
            Environments.Development,
            new Dictionary<string, string?> { ["AllowedHosts"] = allowedHosts },
            cancellationToken: cancellationToken);

        host.App.Should().NotBeNull();
    }

    [Theory]
    [InlineData("app.test")]
    [InlineData("app.test;id.test")]
    [InlineData("app.test:8443")]
    public async Task Named_hosts_pass_outside_Development(string allowedHosts)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var host = await ProductionTestHost.StartAsync(
            Environments.Production,
            new Dictionary<string, string?> { ["AllowedHosts"] = allowedHosts },
            cancellationToken: cancellationToken);

        host.App.Should().NotBeNull();
    }
}
