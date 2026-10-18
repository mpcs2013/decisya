using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Ostium.Authentication.Jwt.Testing;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// #164 G2 D4 (new, G3 M-2): the API has exactly one authentication scheme, <c>Bearer</c>, and it is the
/// default for authenticate and challenge. Decisya's composition adds no second scheme and no policy that
/// replaces the default or the fallback policy (the static half is G6's check of
/// <c>AddDecisyaApiAuthentication</c> and <c>Program.cs</c>).
/// </summary>
[Trait("Category", "Unit")]
public class AuthenticationSchemeSetTests : IDisposable
{
    private readonly TestTokenIssuer _issuer = DecisyaTestTokens.Create();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Exactly_one_scheme_Bearer_is_registered()
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        var provider = factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        var schemes = (await provider.GetAllSchemesAsync()).ToList();

        schemes.Should().ContainSingle();
        schemes[0].Name.Should().Be(JwtBearerDefaults.AuthenticationScheme).And.Be("Bearer");
        (await provider.GetRequestHandlerSchemesAsync()).Should().BeEmpty("no scheme handles requests on its own (no cookie, no OIDC)");
    }

    [Fact]
    public async Task Bearer_is_the_default_authenticate_and_challenge_scheme()
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        var provider = factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        (await provider.GetDefaultAuthenticateSchemeAsync())!.Name.Should().Be("Bearer");
        (await provider.GetDefaultChallengeSchemeAsync())!.Name.Should().Be("Bearer");
        (await provider.GetDefaultForbidSchemeAsync())!.Name.Should().Be("Bearer");
    }

    [Fact]
    public async Task The_default_policy_is_untouched_and_the_fallback_requires_an_authenticated_caller_with_a_sub_claim_on_Bearer_only()
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        var policies = factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        var defaultPolicy = await policies.GetDefaultPolicyAsync();
        var fallback = await policies.GetFallbackPolicyAsync();

        defaultPolicy.AuthenticationSchemes.Should().BeEmpty("the default policy uses the default scheme");
        defaultPolicy.Requirements.Should().ContainSingle().Which.Should().BeOfType<Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement>();
        fallback.Should().NotBeNull("every endpoint needs an authenticated caller unless it opts out");
        // Ostium's fallback policy names Bearer explicitly (observed on 0.1.1 and 0.2.0); with one scheme that is the
        // same outcome as the default scheme. No other scheme may ever appear in it.
        fallback!.AuthenticationSchemes.Should().OnlyContain(scheme => scheme == "Bearer");
        fallback.Requirements.Should().Contain(r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
        fallback.Requirements.Should().Contain(r => r is Microsoft.AspNetCore.Authorization.Infrastructure.ClaimsAuthorizationRequirement
            && ((Microsoft.AspNetCore.Authorization.Infrastructure.ClaimsAuthorizationRequirement)r).ClaimType == "sub");
    }

    [Theory]
    [InlineData("Cookies")]
    [InlineData("OpenIdConnect")]
    [InlineData("Identity.Application")]
    public async Task No_other_scheme_is_registered_by_name(string name)
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        var provider = factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        (await provider.GetSchemeAsync(name)).Should().BeNull();
    }
}
