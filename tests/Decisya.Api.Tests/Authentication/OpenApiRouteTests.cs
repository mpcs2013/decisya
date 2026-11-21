using System.Net;
using System.Net.Http.Headers;
using Microsoft.IdentityModel.Tokens;
using Ostium.Authentication.Jwt.Testing;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// #341 G3 M341-06 (c), R-L15 at runtime: the OpenAPI document is generated in process for the contract
/// test only; no route serves it. In Production an authenticated caller gets 404, not a document, from both
/// the ledger document path and the default one.
/// </summary>
[Trait("Category", "Unit")]
public class OpenApiRouteTests : IDisposable
{
    private readonly TestTokenIssuer _issuer = DecisyaTestTokens.Create();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("/openapi/ledger.json")]
    [InlineData("/openapi/v1.json")]
    public async Task In_Production_no_OpenAPI_document_route_exists_and_an_authenticated_caller_gets_404(string path)
    {
        await using var factory = ApiTestFactory.Create(_issuer, environmentName: "Production");
        using var client = factory.CreateClient();

        var claims = DecisyaTestTokens.WithTenant(_issuer.DefaultClaims(subject: "dev-alice"));
        claims["roles"] = new[] { "tenant-user" };
        var token = TestTokenIssuer.IssueToken(claims, _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256);

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, path);
    }
}
