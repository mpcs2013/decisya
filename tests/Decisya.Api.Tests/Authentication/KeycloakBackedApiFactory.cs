using Decisya.ServiceDefaults.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// Harness B (G2): a real <c>Decisya.Api</c> host pointed at the fixture's Keycloak container
/// — <c>Api:Jwt:Authority</c> is the same base URL string used for the login, and
/// <c>Api:Jwt:RequireHttpsMetadata=false</c> (the container host is loopback).
/// </summary>
internal static class KeycloakBackedApiFactory
{
    internal static WebApplicationFactory<Program> Create(
        KeycloakApiFixture fixture, string? tenancyConnectionString = null, string? entitlementsConnectionString = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            // #21, G2: Program.cs reads ConnectionStrings:tenancy eagerly, before Build() — see
            // ApiTestFactory's remarks for why that requires UseSetting, not
            // ConfigureAppConfiguration. This harness's tests only ever call /api/whoami
            // (SkipTenantMembership), so a placeholder that never connects is enough.
            builder.UseSetting(
                ApiTestFactory.PlaceholderTenancyConnectionStringKey, tenancyConnectionString ?? ApiTestFactory.PlaceholderTenancyConnectionString);
            builder.UseSetting(
                ApiTestFactory.PlaceholderEntitlementsConnectionStringKey, entitlementsConnectionString ?? ApiTestFactory.PlaceholderEntitlementsConnectionString);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            [
                new(DecisyaObservabilityOptions.UserIdHashKeyPath, Canaries.HashKey()),
                new("Api:Jwt:Authority", fixture.Authority),
                new("Api:Jwt:RequireHttpsMetadata", "false"),
            ]));
        });
}
