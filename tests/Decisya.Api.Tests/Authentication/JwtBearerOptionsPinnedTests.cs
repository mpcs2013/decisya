using Decisya.Api.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// G4-20-01's second red test: catches a silent change of a default that no behavioural row
/// would (the framework changing a default, or a future edit accidentally deleting a line).
/// </summary>
public class JwtBearerOptionsPinnedTests : IDisposable
{
    private readonly TestTokenIssuer _issuer = new();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Validation_parameters_are_pinned()
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        using var scope = factory.Services.CreateScope();
        var monitor = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>();

        var options = monitor.Get(JwtBearerDefaults.AuthenticationScheme);
        var parameters = options.TokenValidationParameters;

        parameters.ValidAlgorithms.Should().BeEquivalentTo([SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256]);
        parameters.ValidAudience.Should().Be(ApiJwtDefaults.Audience);
        parameters.ClockSkew.Should().Be(TimeSpan.FromSeconds(60));
        parameters.ValidateIssuer.Should().BeTrue();
        parameters.ValidateAudience.Should().BeTrue();
        parameters.ValidateLifetime.Should().BeTrue();
        parameters.RequireExpirationTime.Should().BeTrue();
        parameters.RequireSignedTokens.Should().BeTrue();
        parameters.ValidateIssuerSigningKey.Should().BeTrue();
        options.SaveToken.Should().BeFalse();
        options.IncludeErrorDetails.Should().BeFalse();
        options.MapInboundClaims.Should().BeFalse();
    }
}
