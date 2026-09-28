using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Decisya.Bff.Tests;

/// <summary>
/// Hosts <c>Decisya.Bff</c> in-process against a real Testcontainers Keycloak and Redis
/// (G2's test harness: "<c>WebApplicationFactory&lt;Program&gt;</c> with <c>Bff:*</c> from the
/// Testcontainers Keycloak... and Redis"). Every test class owns its own instance (never
/// shared) so each can pick its own key-ring directory and environment.
/// </summary>
/// <param name="authority">The realm issuer, e.g. the Testcontainers Keycloak's own
/// <c>http://localhost:&lt;port&gt;/realms/decisya</c>.</param>
/// <param name="clientSecret"><c>decisya-bff</c>'s client secret.</param>
/// <param name="redisConnectionString">The Testcontainers Redis connection string.</param>
/// <param name="keyRingPath">The Data Protection key-ring directory.</param>
/// <param name="environmentName">The ASP.NET Core environment name.</param>
/// <param name="apiAddress">#19: overrides <c>Bff:Api:Address</c>, normally the
/// <c>ApiDouble</c>'s own <c>http://127.0.0.1:&lt;port&gt;</c>. <see langword="null"/> leaves
/// appsettings.json's default (<c>https://decisya-api</c>, never resolved unless a test
/// actually calls <c>/api</c>).</param>
/// <param name="clock">#19: the NodaTime clock <see cref="Decisya.Bff.Session.AccessTokenProvider"/>
/// and <see cref="Decisya.Bff.Session.RedisTicketStore"/> read "now" from. <see langword="null"/>
/// leaves the real <c>SystemClock</c> ServiceDefaults already registers.</param>
/// <param name="backchannelHttpHandler">#19: replaces the OIDC handler's
/// <c>BackchannelHttpHandler</c> (a counting/failing handler for the refresh and end-session
/// calls). Registered the same way as <see cref="BackchannelLogoutTestFactory"/>'s
/// <c>Configure&lt;OpenIdConnectOptions&gt;</c> override — after <c>OidcOptionsSetup</c>, which
/// never touches this property.</param>
internal sealed class BffWebApplicationFactory(
    string authority,
    string clientSecret,
    string redisConnectionString,
    string keyRingPath,
    string environmentName = "Development",
    string? apiAddress = null,
    IClock? clock = null,
    HttpMessageHandler? backchannelHttpHandler = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(environmentName);
        builder.ConfigureAppConfiguration((_, configurationBuilder) =>
        {
            var overrides = new Dictionary<string, string?>
            {
                ["Bff:Oidc:Authority"] = authority,
                ["Bff:Oidc:ClientId"] = "decisya-bff",
                ["Bff:Oidc:ClientSecret"] = clientSecret,
                ["Bff:Oidc:RequireHttpsMetadata"] = "false",
                ["Bff:DataProtection:KeyRingPath"] = keyRingPath,
                ["ConnectionStrings:redis"] = redisConnectionString,
            };

            if (apiAddress is not null)
            {
                overrides["Bff:Api:Address"] = apiAddress;
            }

            configurationBuilder.AddInMemoryCollection(overrides);
        });

        if (clock is not null)
        {
            builder.ConfigureServices(services => services.AddSingleton(clock));
        }

        if (backchannelHttpHandler is not null)
        {
            builder.ConfigureServices(services => services.Configure<OpenIdConnectOptions>(
                OpenIdConnectDefaults.AuthenticationScheme,
                options => options.BackchannelHttpHandler = backchannelHttpHandler));
        }
    }
}
