using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Decisya.Modules.Entitlements.Contracts;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// Issue #26, Story 4 and NFR-41: <c>GET /api/capabilities</c> on the real Api host, with no database.
/// A caller with no tenant never reaches a query (the placeholder database host would fail the request),
/// and a faked <see cref="IEntitlementService"/> drives the shape and fail-closed cases.
/// Test names follow the G1 scenario titles.
/// </summary>
[Trait("Category", "Unit")]
public sealed class CapabilitiesEndpointTests : IDisposable
{
    private const string Path = "/api/capabilities";
    private readonly TestTokenIssuer _issuer = new();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_platform_admin_gets_an_all_false_manifest()
    {
        await using var factory = ApiTestFactory.Create(_issuer, environmentName: "Production");
        using var client = factory.CreateClient();
        var claims = TestTokenIssuer.DefaultClaims(tenantId: null);
        claims["roles"] = new[] { "platform-admin" };
        var token = TestTokenIssuer.IssueToken(claims, _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256);

        using var response = await GetAsync(client, token);

        await AssertAllFalseAsync(response);
    }

    [Fact]
    public async Task A_realm_user_with_no_tenant_gets_an_all_false_manifest()
    {
        await using var factory = ApiTestFactory.Create(_issuer, environmentName: "Production");
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, _issuer.IssueValidAccessToken(tenantId: null));

        await AssertAllFalseAsync(response);
    }

    [Fact]
    public async Task An_anonymous_caller_gets_401()
    {
        await using var factory = ApiTestFactory.Create(_issuer, environmentName: "Production");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Path, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().NotContain("capabilities");
    }

    [Fact]
    public async Task An_evaluation_error_is_never_an_allow()
    {
        var canary = Canaries.Unique("capabilities-exception");
        await using var factory = ApiTestFactory.Create(
            _issuer,
            environmentName: "Production",
            configureServices: services => services.AddScoped<IEntitlementService>(_ => new ThrowingOnSecondKey(canary)));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, _issuer.IssueValidAccessToken(tenantId: null));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain("capabilities").And.NotContain(canary).And.NotContain("true");
        using var document = JsonDocument.Parse(body);
        document.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["type", "title", "status", "traceId"]);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task The_manifest_uses_the_same_decision_as_the_entitlement_service_and_lists_exactly_the_catalog_keys()
    {
        await using var factory = ApiTestFactory.Create(
            _issuer,
            environmentName: "Production",
            configureServices: services => services.AddScoped<IEntitlementService>(_ => new AllowOnlyLedger()));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, _issuer.IssueValidAccessToken(tenantId: null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        document.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("capabilities");
        var capabilities = document.RootElement.GetProperty("capabilities");
        capabilities.EnumerateObject().Select(p => p.Name).Should().Equal(FeatureKeys.All.Select(key => key.Value));
        foreach (var key in FeatureKeys.All)
        {
            capabilities.GetProperty(key.Value).GetBoolean().Should().Be(key == FeatureKeys.LedgerTransactions, key.Value);
        }
    }

    [Fact]
    public async Task The_response_is_no_store_and_the_JSON_options_do_not_rename_dictionary_keys()
    {
        await using var factory = ApiTestFactory.Create(_issuer, environmentName: "Production");
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, _issuer.IssueValidAccessToken(tenantId: null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        factory.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions.DictionaryKeyPolicy.Should().BeNull();
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task AssertAllFalseAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var capabilities = document.RootElement.GetProperty("capabilities");
        capabilities.EnumerateObject().Select(p => p.Name).Should().Equal(FeatureKeys.All.Select(key => key.Value));
        capabilities.EnumerateObject().Should().OnlyContain(p => p.Value.ValueKind == JsonValueKind.False);
    }

    private sealed class ThrowingOnSecondKey(string canary) : IEntitlementService
    {
        private int _calls;

        public Task<bool> IsEnabledAsync(FeatureKey feature, CancellationToken cancellationToken = default) =>
            ++_calls >= 2 ? throw new InvalidOperationException(canary) : Task.FromResult(true);
    }

    private sealed class AllowOnlyLedger : IEntitlementService
    {
        public Task<bool> IsEnabledAsync(FeatureKey feature, CancellationToken cancellationToken = default) =>
            Task.FromResult(feature == FeatureKeys.LedgerTransactions);
    }
}
