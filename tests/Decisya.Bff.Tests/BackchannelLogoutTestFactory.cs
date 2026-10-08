using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Decisya.Bff.Tests;

/// <summary>
/// Hosts <c>Decisya.Bff</c> against a real Redis but a static <c>OpenIdConnectConfiguration</c>
/// (no Keycloak container) for <see cref="BackchannelLogoutTests"/> (G2's test harness note).
/// The optional logger provider (#121) captures the host's log records for the
/// <c>auth.signout</c> event tests.
/// </summary>
internal sealed class BackchannelLogoutTestFactory : WebApplicationFactory<Program>
{
    private readonly string _redisConnectionString;
    private readonly OpenIdConnectConfiguration _configuration;
    private readonly ILoggerProvider? _loggerProvider;

    public BackchannelLogoutTestFactory(
        string redisConnectionString, OpenIdConnectConfiguration configuration, ILoggerProvider? loggerProvider = null)
    {
        _redisConnectionString = redisConnectionString;
        _configuration = configuration;
        _loggerProvider = loggerProvider;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configurationBuilder) =>
            configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bff:Oidc:Authority"] = "https://unused.example/realms/decisya",
                ["Bff:Oidc:ClientId"] = "decisya-bff",
                ["Bff:Oidc:ClientSecret"] = Canaries.Unique("client-secret"),
                ["Bff:Oidc:RequireHttpsMetadata"] = "true",
                ["Bff:DataProtection:KeyRingPath"] = BffFactoryFactory.CreateTemporaryKeyRingDirectory(),
                ["ConnectionStrings:redis"] = _redisConnectionString,
            }));

        if (_loggerProvider is not null)
        {
            builder.ConfigureLogging(logging => logging.AddProvider(_loggerProvider));
        }

        // Short-circuits discovery entirely: the OIDC handler's own post-configure builds a
        // static ConfigurationManager from this when Authority-based discovery is never
        // reached, so no request to the fake Authority above ever happens.
        builder.ConfigureServices(services =>
            services.Configure<OpenIdConnectOptions>(
                OpenIdConnectDefaults.AuthenticationScheme, options => options.Configuration = _configuration));
    }
}
