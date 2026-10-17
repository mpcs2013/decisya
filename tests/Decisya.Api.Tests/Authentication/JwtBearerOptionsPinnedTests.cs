using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Logging;
using Microsoft.IdentityModel.Tokens;
using Ostium.Authentication.Jwt.Testing;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// G4-20-01's second red test: catches a silent change of a default that no behavioural row
/// would (the framework changing a default, a package release changing a pinned value, or a future
/// edit accidentally deleting a line). Since #164 the options are set by
/// <c>Ostium.Authentication.Jwt</c>; this test reads them from Decisya's real host, built through
/// <c>AddDecisyaApiAuthentication</c> (G3 M-2), because it is the compensating control for the
/// missing 0.1.1 report.
/// </summary>
public class JwtBearerOptionsPinnedTests : IDisposable
{
    private readonly TestTokenIssuer _issuer = DecisyaTestTokens.Create();

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
        parameters.ValidAudience.Should().Be(DecisyaTestTokens.Audience);
        parameters.ValidIssuer.Should().Be(DecisyaTestTokens.Issuer);
        parameters.ClockSkew.Should().Be(TimeSpan.FromSeconds(60));
        parameters.ValidateIssuer.Should().BeTrue();
        parameters.ValidateAudience.Should().BeTrue();
        parameters.ValidateLifetime.Should().BeTrue();
        parameters.RequireExpirationTime.Should().BeTrue();
        parameters.RequireSignedTokens.Should().BeTrue();
        parameters.ValidateIssuerSigningKey.Should().BeTrue();
        parameters.NameClaimType.Should().Be("sub");
        options.SaveToken.Should().BeFalse();
        options.IncludeErrorDetails.Should().BeFalse();
        options.MapInboundClaims.Should().BeFalse();
        options.RefreshInterval.Should().Be(BaseConfigurationManager.DefaultRefreshInterval);
        options.AutomaticRefreshInterval.Should().Be(BaseConfigurationManager.DefaultAutomaticRefreshInterval);
    }

    [Fact]
    public async Task The_valid_issuer_is_the_authority_with_the_trailing_slash_trimmed()
    {
        await using var factory = ApiTestFactory.Create(
            _issuer, extraConfiguration: [new("Api:Jwt:Authority", DecisyaTestTokens.Issuer + "/")]);
        using var scope = factory.Services.CreateScope();

        var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        options.TokenValidationParameters.ValidIssuer.Should().Be(DecisyaTestTokens.Issuer);
    }

    [Fact]
    public async Task Identity_model_PII_and_artifact_logging_are_off_at_run_time()
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        using var scope = factory.Services.CreateScope();
        _ = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        IdentityModelEventSource.ShowPII.Should().BeFalse();
        IdentityModelEventSource.LogCompleteSecurityArtifact.Should().BeFalse();
    }
}
