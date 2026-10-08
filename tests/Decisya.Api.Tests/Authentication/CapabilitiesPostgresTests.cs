using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// Issue #26, Story 4, end to end: the real Api host, the real Entitlements service and a real, fully
/// migrated Postgres database. Trials and overrides are created through the real admin endpoints (#25),
/// so the manifest is checked against the same state the enforcing service reads. Every test has its own
/// database. Test names follow the G1 scenario titles.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CapabilitiesPostgresTests(TenancyDatabaseFixture database) : IDisposable
{
    private const string Ledger = "ledger.transactions";
    private const string Scenarios = "forecasting.scenarios";
    private readonly TestTokenIssuer _issuer = new();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_tenant_on_Free_gets_Free_capabilities_only()
    {
        await using var stack = await StartAsync();
        var tenant = Guid.NewGuid();

        using var response = await stack.CapabilitiesAsync(tenant, Canaries.Unique("alice"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await BodyAsync(response)).Should().Be("""{"capabilities":{"ledger.transactions":true,"forecasting.scenarios":false}}""");
    }

    [Fact]
    public async Task A_tenant_on_a_Pro_trial_gets_Pro_capabilities()
    {
        await using var stack = await StartAsync();
        var tenant = Guid.NewGuid();
        var alice = Canaries.Unique("alice");
        (await ValuesAsync(await stack.CapabilitiesAsync(tenant, alice))).Should().Equal([(Ledger, true), (Scenarios, false)]);

        using (var trial = await stack.AdminAsync("POST", $"/api/admin/tenants/{tenant}/trial"))
        {
            trial.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        (await ValuesAsync(await stack.CapabilitiesAsync(tenant, alice))).Should().Equal([(Ledger, true), (Scenarios, true)]);
    }

    [Fact]
    public async Task An_override_is_reflected_and_the_manifest_is_tenant_scoped()
    {
        await using var stack = await StartAsync();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var alice = Canaries.Unique("alice");
        var bob = Canaries.Unique("bob");
        (await ValuesAsync(await stack.CapabilitiesAsync(tenantA, alice))).Should().Contain((Scenarios, false));
        (await ValuesAsync(await stack.CapabilitiesAsync(tenantB, bob))).Should().Contain((Scenarios, false));

        using (var grant = await stack.AdminAsync("PUT", $"/api/admin/tenants/{tenantA}/overrides/{Scenarios}", """{"reason":"Pilot"}"""))
        {
            grant.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        (await ValuesAsync(await stack.CapabilitiesAsync(tenantA, alice))).Should().Equal([(Ledger, true), (Scenarios, true)]);
        (await ValuesAsync(await stack.CapabilitiesAsync(tenantB, bob))).Should().Equal([(Ledger, true), (Scenarios, false)]);
    }

    [Fact]
    public async Task A_caller_who_is_not_a_member_of_the_tenant_gets_the_membership_gate_403_and_no_body_detail()
    {
        await using var stack = await StartAsync();
        var tenant = Guid.NewGuid();
        using (await stack.CapabilitiesAsync(tenant, Canaries.Unique("owner")))
        {
        }

        using var response = await stack.CapabilitiesAsync(tenant, Canaries.Unique("stranger"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await BodyAsync(response);
        body.Should().NotContain("capabilities");
        using var document = JsonDocument.Parse(body);
        document.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["type", "title", "status", "traceId"]);
    }

    private async Task<Stack> StartAsync()
    {
        var connectionString = await database.CreateFullyMigratedDatabaseAsync(TestContext.Current.CancellationToken);
        var factory = ApiTestFactory.Create(
            _issuer,
            environmentName: "Production",
            extraConfiguration:
            [
                new("ConnectionStrings:tenancy", connectionString),
                new("ConnectionStrings:entitlements", connectionString),
            ]);
        return new Stack(factory, factory.CreateClient(), _issuer);
    }

    private static async Task<string> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    private static async Task<List<(string Key, bool Value)>> ValuesAsync(HttpResponseMessage response)
    {
        using (response)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await BodyAsync(response));
            return document.RootElement.GetProperty("capabilities").EnumerateObject()
                .Select(p => (p.Name, p.Value.GetBoolean()))
                .ToList();
        }
    }

    private sealed class Stack(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, HttpClient client, TestTokenIssuer issuer) : IAsyncDisposable
    {
        public Task<HttpResponseMessage> CapabilitiesAsync(Guid tenant, string subject) =>
            SendAsync(HttpMethod.Get, "/api/capabilities", issuer.IssueValidAccessToken(subject: subject, tenantId: tenant.ToString()), null);

        public Task<HttpResponseMessage> AdminAsync(string method, string path, string? body = null)
        {
            var claims = TestTokenIssuer.DefaultClaims(subject: "3f2d1c9a-8b7e-4d6c-a5f4-0e1d2c3b4a59", tenantId: null);
            claims["roles"] = new[] { "platform-admin" };
            claims["acr"] = "2"; // #121: the MFA proof Keycloak's step-up flow gives an admin after the OTP.
            var token = TestTokenIssuer.IssueToken(claims, issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256);
            return SendAsync(new HttpMethod(method), path, token, body);
        }

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, string? body)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            return await client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await factory.DisposeAsync();
        }
    }
}
