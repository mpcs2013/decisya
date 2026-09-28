using Decisya.ServiceDefaults.Logging;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// Harness A (G2): a <c>Decisya.Api</c> host whose <c>JwtBearerOptions.ConfigurationManager</c>
/// is replaced with a <see cref="StaticConfigurationManager{T}"/> built from
/// <see cref="TestTokenIssuer"/>'s keys, so no test ever contacts the network. The
/// <c>ConfigurationManager</c> must be replaced wholesale, not <c>Configuration</c> alone: the
/// framework's own post-configure has already built a manager from <c>Authority</c> by the
/// time a test's <c>ConfigureTestServices</c> callback runs.
/// </summary>
internal static class ApiTestFactory
{
    internal static WebApplicationFactory<Program> Create(
        TestTokenIssuer issuer,
        string environmentName = "Development",
        IConfigurationManager<OpenIdConnectConfiguration>? configurationManager = null,
        Action<IServiceCollection>? configureServices = null,
        IEnumerable<KeyValuePair<string, string?>>? extraConfiguration = null,
        string? testEndpointsCanary = null,
        Action<ILoggingBuilder>? configureLogging = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environmentName);

            if (configureLogging is not null)
            {
                builder.ConfigureLogging(configureLogging);
            }
            builder.ConfigureAppConfiguration((_, config) =>
            {
                // A Dictionary, not a List: MemoryConfigurationProvider throws
                // ArgumentException ("An item with the same key has already been added") on a
                // duplicate key within one AddInMemoryCollection call — it does not take the
                // last value the way appsettings*.json layering does. A caller's
                // extraConfiguration overriding a default key (Api:Jwt:Authority,
                // Api:Jwt:RequireHttpsMetadata) must replace it here, not append a duplicate.
                var values = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [DecisyaObservabilityOptions.UserIdHashKeyPath] = Canaries.HashKey(),
                    ["Api:Jwt:Authority"] = TestTokenIssuer.Issuer,
                };

                if (extraConfiguration is not null)
                {
                    foreach (var pair in extraConfiguration)
                    {
                        values[pair.Key] = pair.Value;
                    }
                }

                config.AddInMemoryCollection(values);
            });

            builder.ConfigureTestServices(services =>
            {
                var manager = configurationManager ?? BuildDefaultConfigurationManager(issuer);

                services.PostConfigure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    options => options.ConfigurationManager = manager);

                if (testEndpointsCanary is not null)
                {
                    services.AddSingleton<IStartupFilter>(new TestOnlyEndpointsStartupFilter(testEndpointsCanary));
                }

                configureServices?.Invoke(services);
            });
        });

    private static StaticConfigurationManager<OpenIdConnectConfiguration> BuildDefaultConfigurationManager(TestTokenIssuer issuer)
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = TestTokenIssuer.Issuer };
        configuration.SigningKeys.Add(issuer.RsaSigningKey);
        configuration.SigningKeys.Add(issuer.EcSigningKey);
        return new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
    }
}
