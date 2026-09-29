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
/// <remarks>
/// <see cref="Decisya.Api"/>'s <c>Program.cs</c> reads <c>ConnectionStrings:tenancy</c> eagerly
/// (<c>builder.Configuration.GetConnectionString("tenancy")</c>, before <c>Build()</c>, to pass
/// to <c>AddTenancyModule</c>). Found empirically (#21): <c>WithWebHostBuilder</c>'s
/// <c>ConfigureAppConfiguration</c>/<c>AddInMemoryCollection</c> override is applied by
/// <c>WebApplicationFactory</c>'s <c>HostFactoryResolver</c> interception only at the moment
/// <c>Build()</c> itself runs — a diagnostic added directly to <c>Program.cs</c> and removed
/// again confirmed <c>builder.Configuration.GetConnectionString("tenancy")</c> is still empty
/// immediately before that line, and already carries the override immediately after — so any
/// value Program.cs reads eagerly, before <c>Build()</c>, never sees a
/// <c>ConfigureAppConfiguration</c> override. <c>IWebHostBuilder.UseSetting</c> (the same
/// mechanism behind the already-reliable <c>UseEnvironment</c> call below) is applied earlier,
/// so it is the only reliable way to hand this specific key to Program.cs's eager read.
/// </remarks>
internal static class ApiTestFactory
{
    internal const string PlaceholderTenancyConnectionStringKey = "ConnectionStrings:tenancy";

    internal const string PlaceholderTenancyConnectionString =
        "Host=db.invalid;Port=5432;Database=decisya;Username=decisya_tenancy;Password=placeholder";

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

            // #21, G2: AddTenancyModule throws if ConnectionStrings:tenancy is missing. A
            // placeholder host (Host=db.invalid) never actually connects, so every test that
            // never touches the Tenancy database (most of this project) still builds; a query
            // attempted against it would throw, so a 403 returned by a test using this
            // placeholder is itself proof no query ran. A caller's extraConfiguration can
            // override this key (Category=Integration tests that need a real database); see the
            // remarks above for why that override must go through UseSetting, not
            // ConfigureAppConfiguration, to actually reach Program.cs's eager read.
            var tenancyConnectionString = PlaceholderTenancyConnectionString;
            var remainingConfiguration = new List<KeyValuePair<string, string?>>();
            if (extraConfiguration is not null)
            {
                foreach (var pair in extraConfiguration)
                {
                    if (string.Equals(pair.Key, PlaceholderTenancyConnectionStringKey, StringComparison.Ordinal))
                    {
                        tenancyConnectionString = pair.Value ?? PlaceholderTenancyConnectionString;
                    }
                    else
                    {
                        remainingConfiguration.Add(pair);
                    }
                }
            }

            builder.UseSetting(PlaceholderTenancyConnectionStringKey, tenancyConnectionString);

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
                // ConnectionStrings:tenancy is deliberately excluded here (handled above via
                // UseSetting instead): everything else Program.cs and its entry points read is
                // consumed lazily (through IOptions/IConfigurationSection bound after Build()),
                // so ConfigureAppConfiguration's later-applied override reaches it correctly.
                var values = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [DecisyaObservabilityOptions.UserIdHashKeyPath] = Canaries.HashKey(),
                    ["Api:Jwt:Authority"] = TestTokenIssuer.Issuer,
                };

                foreach (var pair in remainingConfiguration)
                {
                    values[pair.Key] = pair.Value;
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
