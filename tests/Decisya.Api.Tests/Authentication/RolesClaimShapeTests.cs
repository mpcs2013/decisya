using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Entitlements.Contracts.Admin;
using Decisya.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Ostium.Authentication.Jwt.Testing;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// #164 Story 2, scenario 2 (issue comment of 2026-10-15): a <c>roles</c> string array handed to
/// <c>TestTokenIssuer.IssueAccessToken</c> reaches the API as a JSON array of strings and maps to roles
/// exactly as before. If the shape were wrong, the defect goes to the supplier in the exchange; there is no
/// adapter and no workaround in Decisya, so these assertions are never adapted.
/// </summary>
[Trait("Category", "Unit")]
public class RolesClaimShapeTests : IDisposable
{
    private const string AdminRole = "platform-admin";
    private const string Tenant = DecisyaTestTokens.DevAliceTenantId;
    private const string TrialPath = "/api/admin/tenants/" + Tenant + "/trial";

    private readonly TestTokenIssuer _issuer = DecisyaTestTokens.Create();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_roles_string_array_is_a_JSON_array_of_two_strings_in_the_token_payload()
    {
        var token = _issuer.IssueAccessToken("dev-admin-sub", AdditionalClaims(withTenant: false));

        using var payload = DecodePayload(token);
        var roles = payload.RootElement.GetProperty("roles");

        roles.ValueKind.Should().Be(JsonValueKind.Array, "a joined string would not map to two roles");
        roles.GetArrayLength().Should().Be(2);
        roles.EnumerateArray().Select(r => r.ValueKind).Should().OnlyContain(kind => kind == JsonValueKind.String);
        roles.EnumerateArray().Select(r => r.GetString()).Should().Equal("tenant-user", AdminRole);
        payload.RootElement.GetProperty("acr").GetString().Should().Be("2");
    }

    [Fact]
    public async Task The_principal_is_a_platform_admin_and_the_admin_endpoint_succeeds_with_no_tenant()
    {
        var stub = new RecordingAdminCommands();
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        using var response = await PostAsync(client, _issuer.IssueAccessToken("dev-admin-sub", AdditionalClaims(withTenant: false)));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        stub.Calls.Should().Be(1);
    }

    [Fact]
    public async Task With_a_tenant_id_next_to_the_admin_role_the_result_is_the_same_403_as_before()
    {
        var stub = new RecordingAdminCommands();
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        using var response = await PostAsync(client, _issuer.IssueAccessToken("dev-admin-sub", AdditionalClaims(withTenant: true)));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "R-2: the dual claim is refused");
        stub.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_single_role_array_without_the_admin_role_is_refused()
    {
        var stub = new RecordingAdminCommands();
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();
        var claims = new Dictionary<string, object> { ["roles"] = new[] { "tenant-user" }, ["acr"] = "2" };

        using var response = await PostAsync(client, _issuer.IssueAccessToken("dev-admin-sub", claims));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        stub.Calls.Should().Be(0);
    }

    private static Dictionary<string, object> AdditionalClaims(bool withTenant)
    {
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["roles"] = new[] { "tenant-user", AdminRole },
            ["acr"] = "2",
        };
        if (withTenant)
        {
            claims["tenant_id"] = Tenant;
        }

        return claims;
    }

    private static JsonDocument DecodePayload(string token)
    {
        var segment = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        segment = segment.PadRight(segment.Length + ((4 - (segment.Length % 4)) % 4), '=');
        return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(segment)));
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> CreateFactory(RecordingAdminCommands stub) =>
        ApiTestFactory.Create(
            _issuer,
            environmentName: "Production",
            configureServices: services => services.AddScoped<IEntitlementAdminCommands>(_ => stub));

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TrialPath)
        {
            Content = new StringContent("""{"reason":"ok"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed class RecordingAdminCommands : IEntitlementAdminCommands
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
