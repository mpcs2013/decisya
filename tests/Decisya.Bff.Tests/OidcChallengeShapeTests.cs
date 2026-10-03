using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Decisya.Bff.Tests;

/// <summary>
/// G5 (issue #18 traceability): Story 1's third scenario, the shape of the handler that
/// builds the authorization request — <c>Decisya.Bff.Session.OidcOptionsSetup</c>'s own
/// configuration of <c>OpenIdConnectOptions</c>, read back through the same options-monitor
/// pattern <c>SessionFixationGuard</c> and <c>LogoutTokenValidator</c> use. No Docker: this
/// never reaches Keycloak, so it runs in the unit lane, the same style as
/// <see cref="BffOptionsTests"/>.
/// </summary>
public class OidcChallengeShapeTests
{
    [Fact]
    public void The_OIDC_handler_is_configured_for_authorization_code_flow_with_PKCE()
    {
        using var factory = new DevelopmentFactory();
        var oidcOptionsMonitor = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();
        var options = oidcOptionsMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme);

        options.ResponseType.Should().Be(OpenIdConnectResponseType.Code);
        options.ResponseMode.Should().Be(OpenIdConnectResponseMode.Query);
        options.UsePkce.Should().BeTrue(
            "the ASP.NET Core OIDC handler only implements PKCE with S256; UsePkce=true is exactly the code_challenge_method=S256 the Gherkin scenario names");
    }

    /// <summary>#26 G2 D4: the logout rewrite reads the handler's 302 <c>Location</c>, which only
    /// exists with <c>RedirectGet</c> (a <c>FormPost</c> sign-out writes an inline-script HTML body the
    /// CSP blocks). <c>RemoteSignOutPath</c> stays empty (#18 S-7).</summary>
    [Fact]
    public void The_OIDC_sign_out_uses_RedirectGet_and_exposes_no_remote_sign_out_path()
    {
        using var factory = new DevelopmentFactory();
        var options = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);

        options.AuthenticationMethod.Should().Be(OpenIdConnectRedirectBehavior.RedirectGet);
        options.RemoteSignOutPath.HasValue.Should().BeFalse();
    }

    private sealed class DevelopmentFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
                configurationBuilder.AddInMemoryCollection(TestConfiguration.GoodOverrides()));
        }
    }
}
