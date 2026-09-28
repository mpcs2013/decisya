using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// Story 3 (the #19 boundary): the authenticated user and tenant come only from validated
/// token claims, never from a client-supplied header.
/// </summary>
public class CallerIdentityTests : IDisposable
{
    private const string ClaimTenantId = "7c9e6679-7425-40de-944b-e07fc1f90ae7";
    private const string HeaderTenantId = "2f1a8d5e-3b4c-4e6f-9a7b-1c2d3e4f5a6b";

    private readonly TestTokenIssuer _issuer = new();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_client_supplied_tenant_header_is_ignored()
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        using var client = factory.CreateClient();
        var token = _issuer.IssueValidAccessToken(tenantId: ClaimTenantId);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("X-Tenant-Id", HeaderTenantId);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("tenantId").GetString().Should().Be(ClaimTenantId);
    }

    [Fact]
    public async Task A_client_supplied_user_header_is_ignored()
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        using var client = factory.CreateClient();
        var token = _issuer.IssueValidAccessToken(subject: "dev-alice");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("X-User-Id", "someone-elses-id");
        request.Headers.TryAddWithoutValidation("X-Forwarded-User", "someone-elses-id");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "attacker.test");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("userId").GetString().Should().Be("dev-alice");
    }

    [Fact]
    public async Task A_client_supplied_tenant_header_does_not_fill_a_gap_when_the_token_carries_no_tenant_id()
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        using var client = factory.CreateClient();
        var token = _issuer.IssueValidAccessToken(tenantId: null);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("X-Tenant-Id", HeaderTenantId);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var bodyText = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(bodyText);
        body.RootElement.TryGetProperty("tenantId", out _).Should().BeFalse();
        bodyText.Should().NotContain(HeaderTenantId);
    }
}
