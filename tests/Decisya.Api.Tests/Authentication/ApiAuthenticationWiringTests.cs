using Decisya.Api.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// G6 review (F2): since the #20 flake fix moved <see cref="ApiJwtOptionsTests"/>'
/// Development-only-relaxation rows off <c>WebApplicationFactory&lt;Program&gt;</c> and onto
/// <see cref="ApiJwtOptionsEnvironmentValidator"/> directly, nothing was left proving that
/// <c>AddApiAuthentication</c> actually *wires* that validator (and <c>.ValidateOnStart()</c>)
/// into the DI container. This resolves <see cref="IStartupValidator"/> and calls it directly,
/// without ever starting the host (no <c>Build()</c>-then-<c>Run()</c>, no
/// <c>WebApplicationFactory</c>), so it cannot hit the host-start race those tests were moved
/// away from.
/// </summary>
public class ApiAuthenticationWiringTests
{
    [Fact]
    public void AddApiAuthentication_wires_the_environment_validator_into_ValidateOnStart()
    {
        var canary = $"http://canary-{Guid.NewGuid():N}.test/realms/decisya";
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Configuration.AddInMemoryCollection([new("Api:Jwt:Authority", canary)]);

        builder.AddApiAuthentication();

        using var app = builder.Build();

        var validator = app.Services.GetRequiredService<IStartupValidator>();

        var exception = Record.Exception(validator.Validate);

        exception.Should().BeOfType<OptionsValidationException>(
            "AddApiAuthentication must register ApiJwtOptionsEnvironmentValidator and call ValidateOnStart()");
        exception!.Message.Should().NotContain(canary, "the failure message names the key, never the value");
    }
}
