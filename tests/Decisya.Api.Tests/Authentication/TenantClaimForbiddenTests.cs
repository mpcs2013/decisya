using System.Net;
using System.Net.Http.Headers;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// G3 G4-21-02 (closes #22 B-2; T-03, T-04): a malformed <c>tenant_id</c> claim is refused
/// before routing, on every path — including one with no mapped endpoint — and with no
/// database access. The host under test carries only the placeholder
/// <c>Host=db.invalid</c> tenancy connection string (<see cref="ApiTestFactory"/>'s default), so
/// a query attempted against it would throw and turn the 403 into a 500: a 403 here is itself
/// proof that <c>CallerContextMiddleware</c> rejected the request before any endpoint, handler
/// or <c>TenantDbContext</c> query ran.
/// </summary>
public class TenantClaimForbiddenTests : IDisposable
{
    private readonly TestTokenIssuer _issuer = new();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("0x9e6679-7425-40de-944b-e07fc1f90ae7")]
    public async Task A_malformed_tenant_id_claim_gives_403_before_routing_with_no_database_access(string malformedTenantId)
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        using var client = factory.CreateClient();
        var token = _issuer.IssueValidAccessToken(tenantId: malformedTenantId);

        foreach (var path in new[] { "/api/tenancy/me", "/api/tenancy/members", "/api/does-not-exist" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, path);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            body.Should().NotContain(malformedTenantId, path);
            body.Should().Contain("\"status\"", path);
        }
    }
}
