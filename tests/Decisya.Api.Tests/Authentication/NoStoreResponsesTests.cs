using System.Net;
using System.Net.Http.Headers;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// #25 G2 D5: <c>Cache-Control: no-store</c> on every API response, whatever produced it: the 401
/// challenge, a 403, a 404, a 200 and the exception handler's 500. <c>UseNoStoreResponses</c> runs
/// first, so even a response the exception handler rebuilds carries it.
/// </summary>
public class NoStoreResponsesTests : IDisposable
{
    private readonly TestTokenIssuer _issuer = new();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task The_401_challenge_carries_no_store()
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/whoami", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task A_200_a_404_and_a_pre_routing_403_carry_no_store()
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        using var client = factory.CreateClient();
        var token = _issuer.IssueValidAccessToken(tenantId: null);

        using var ok = await GetAsync(client, "/api/whoami", token);
        using var notFound = await GetAsync(client, "/api/does-not-exist", token);
        using var forbidden = await GetAsync(client, "/api/whoami", _issuer.IssueValidAccessToken(subject: "   "));

        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        notFound.StatusCode.Should().Be(HttpStatusCode.NotFound);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        foreach (var response in new[] { ok, notFound, forbidden })
        {
            response.Headers.CacheControl!.NoStore.Should().BeTrue(response.StatusCode.ToString());
        }
    }

    [Fact]
    public async Task The_anonymous_health_endpoints_carry_no_store()
    {
        await using var factory = ApiTestFactory.Create(_issuer, environmentName: "Development");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/alive", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task The_exception_handlers_500_carries_no_store()
    {
        await using var factory = ApiTestFactory.Create(_issuer, testEndpointsCanary: Canaries.Unique("no-store"));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, TestOnlyEndpointsStartupFilter.ThrowPath, _issuer.IssueValidAccessToken());

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
