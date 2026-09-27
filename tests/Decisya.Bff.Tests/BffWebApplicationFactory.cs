using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Decisya.Bff.Tests;

/// <summary>
/// Hosts <c>Decisya.Bff</c> in-process against a real Testcontainers Keycloak and Redis
/// (G2's test harness: "<c>WebApplicationFactory&lt;Program&gt;</c> with <c>Bff:*</c> from the
/// Testcontainers Keycloak... and Redis"). Every test class owns its own instance (never
/// shared) so each can pick its own key-ring directory and environment.
/// </summary>
internal sealed class BffWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _authority;
    private readonly string _clientSecret;
    private readonly string _redisConnectionString;
    private readonly string _keyRingPath;
    private readonly string _environmentName;

    public BffWebApplicationFactory(
        string authority,
        string clientSecret,
        string redisConnectionString,
        string keyRingPath,
        string environmentName = "Development")
    {
        _authority = authority;
        _clientSecret = clientSecret;
        _redisConnectionString = redisConnectionString;
        _keyRingPath = keyRingPath;
        _environmentName = environmentName;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(_environmentName);
        builder.ConfigureAppConfiguration((_, configurationBuilder) =>
        {
            configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bff:Oidc:Authority"] = _authority,
                ["Bff:Oidc:ClientId"] = "decisya-bff",
                ["Bff:Oidc:ClientSecret"] = _clientSecret,
                ["Bff:Oidc:RequireHttpsMetadata"] = "false",
                ["Bff:DataProtection:KeyRingPath"] = _keyRingPath,
                ["ConnectionStrings:redis"] = _redisConnectionString,
            });
        });
    }
}
