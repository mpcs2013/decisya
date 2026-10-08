using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Decisya.Identity.Tests;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Entitlements.Contracts.Admin;
using Decisya.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NodaTime;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// Issue #121, G3 G4-121-01 (c): the Api's <c>/api/admin</c> policy against tokens the real
/// <b>production</b> realm issues (imported through the real wrapper into the pinned Keycloak, with a
/// synthetic admin and a TOTP created at test time). The Api runs in <c>Production</c> with
/// <c>RequireAdminMfa</c> on its default (true) and a static signing-key set read from Keycloak.
/// </summary>
[Trait("Category", "Integration")]
[Collection(ProductionKeycloakDefinition.Name)]
public sealed class AdminMfaProductionRealmTests : IDisposable
{
    private readonly ProductionKeycloak _keycloak;
    private readonly TestTokenIssuer _unusedIssuer = new();

    public AdminMfaProductionRealmTests(ProductionKeycloak keycloak)
    {
        _keycloak = keycloak;
    }

    public void Dispose()
    {
        _unusedIssuer.Dispose();
    }

    [Fact]
    public async Task An_admin_with_totp_gets_acr_2_and_is_accepted_and_a_tenant_user_gets_acr_1_and_is_refused_403()
    {
        var ct = TestContext.Current.CancellationToken;
        await _keycloak.EnsureStartedAsync(ct);
        var admin = await _keycloak.CreateUserAsync(ProductionKeycloak.AdminRole, withTotp: true, ct);
        var tenant = await _keycloak.CreateUserAsync(ProductionKeycloak.TenantRole, withTotp: false, ct);
        var commands = new CountingCommands();
        var provider = new CapturingLoggerProvider();
        await using var factory = await CreateFactoryAsync(commands, provider, ct);
        using var client = factory.CreateClient();

        var adminToken = (await _keycloak.ExchangeAsync(
            await _keycloak.LoginAsync(admin.Username, admin.Password, "2", admin.TotpSecret, ct), ct)).AccessToken;
        var tenantToken = (await _keycloak.ExchangeAsync(
            await _keycloak.LoginAsync(tenant.Username, tenant.Password, "2", null, ct), ct)).AccessToken;

        ProductionKeycloak.ReadAcr(adminToken).Value.Should().Be("2");
        ProductionKeycloak.ReadAcr(tenantToken).Value.Should().Be("1");

        var trial = $"/api/admin/tenants/{ProductionKeycloak.DefaultTenantId}/trial";

        using (var accepted = await SendAsync(client, "POST", trial, adminToken, ct))
        {
            accepted.StatusCode.Should().Be(HttpStatusCode.NoContent, "an MFA-proven platform admin reaches /api/admin");
        }

        commands.Calls.Should().Be(1);
        provider.Records.Where(r => r.EventName == "auth.admin.mfa_required").Should().BeEmpty();

        provider.Records.Clear();
        using (var refused = await SendAsync(client, "POST", trial, tenantToken, ct))
        {
            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await refused.Content.ReadAsStringAsync(ct)).Should().Contain("traceId").And.NotContain(tenant.Username);
        }

        using (var whoAmI = await SendAsync(client, "GET", "/api/whoami", tenantToken, ct))
        {
            whoAmI.StatusCode.Should().Be(HttpStatusCode.OK, "the tenant user's token itself is valid; only /api/admin refuses it");
        }

        commands.Calls.Should().Be(1, "a refused caller never reaches the commands");
    }

    /// <summary>Without the BFF's acr_values=2 an admin signs in at level 1: the Api, not the browser, refuses it.</summary>
    [Fact]
    public async Task An_admin_token_issued_without_the_step_up_is_refused_with_one_mfa_warning()
    {
        var ct = TestContext.Current.CancellationToken;
        await _keycloak.EnsureStartedAsync(ct);
        var admin = await _keycloak.CreateUserAsync(ProductionKeycloak.AdminRole, withTotp: true, ct);
        var commands = new CountingCommands();
        var provider = new CapturingLoggerProvider();
        await using var factory = await CreateFactoryAsync(commands, provider, ct);
        using var client = factory.CreateClient();

        var token = (await _keycloak.ExchangeAsync(
            await _keycloak.LoginAsync(admin.Username, admin.Password, null, admin.TotpSecret, ct), ct)).AccessToken;
        ProductionKeycloak.ReadAcr(token).Value.Should().Be("1");

        using var refused = await SendAsync(
            client, "POST", $"/api/admin/tenants/{ProductionKeycloak.DefaultTenantId}/trial", token, ct);

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        commands.Calls.Should().Be(0);
        var warnings = provider.Records.Where(r => r.EventName == "auth.admin.mfa_required").ToList();
        warnings.Should().ContainSingle();
        warnings[0].Level.Should().Be(LogLevel.Warning);
        warnings[0].Contains(admin.Username).Should().BeFalse();
    }

    // ------------------------------------------------------------------ helpers

    private async Task<WebApplicationFactory<Program>> CreateFactoryAsync(
        CountingCommands commands, CapturingLoggerProvider provider, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(_keycloak.BaseAddress) };
        http.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        var jwks = await http.GetStringAsync($"/realms/{ProductionKeycloak.Realm}/protocol/openid-connect/certs", ct);

        var configuration = new OpenIdConnectConfiguration { Issuer = ProductionKeycloak.Issuer };
        foreach (var key in new JsonWebKeySet(jwks).GetSigningKeys())
        {
            configuration.SigningKeys.Add(key);
        }

        configuration.SigningKeys.Should().NotBeEmpty();

        return ApiTestFactory.Create(
            _unusedIssuer,
            environmentName: "Production",
            configurationManager: new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration),
            extraConfiguration: [new("Api:Jwt:Authority", ProductionKeycloak.Issuer)],
            configureServices: services => services.AddScoped<IEntitlementAdminCommands>(_ => commands),
            configureLogging: logging => logging.AddProvider(provider));
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (method == "POST")
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, ct);
    }

    private sealed class CountingCommands : IEntitlementAdminCommands
    {
        private int _calls;

        public int Calls => _calls;

        public Task<EntitlementAdminResult> StartTrialAsync(TenantId tenantId, CancellationToken cancellationToken = default) => Record();

        public Task<EntitlementAdminResult> GrantOverrideAsync(
            TenantId tenantId, FeatureKey feature, string reason, Instant? expiresAt, CancellationToken cancellationToken = default) => Record();

        public Task<EntitlementAdminResult> RevokeOverrideAsync(
            TenantId tenantId, FeatureKey feature, CancellationToken cancellationToken = default) => Record();

        private Task<EntitlementAdminResult> Record()
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(EntitlementAdminResult.Succeeded);
        }
    }
}
