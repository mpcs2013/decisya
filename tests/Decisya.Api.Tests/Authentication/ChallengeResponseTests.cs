using System.Net;
using System.Net.Http.Headers;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// G4-20-03 (T-06; Story 2 "no detail"): every rejection carries nothing an attacker could use
/// as an oracle — the same status, the same bare <c>WWW-Authenticate: Bearer</c>, the same
/// empty body, and the same header set as the plain "no token at all" baseline.
/// </summary>
public class ChallengeResponseTests : IDisposable
{
    private readonly TestTokenIssuer _issuer = new();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task All_rejections_are_identical()
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        using var client = factory.CreateClient();

        using var baselineResponse = await client.GetAsync("/api/whoami", TestContext.Current.CancellationToken);
        await AssertBareChallengeAsync(baselineResponse, "no Authorization header");
        var baselineHeaderNames = HeaderNames(baselineResponse);

        foreach (var (label, buildToken) in InvalidTokenCaseCatalog.Cases())
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", buildToken(_issuer));
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

            await AssertBareChallengeAsync(response, label);
            HeaderNames(response).Should().BeEquivalentTo(baselineHeaderNames, label);
        }

        using var basicRequest = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        basicRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", "dXNlcjpwYXNz");
        using var basicResponse = await client.SendAsync(basicRequest, TestContext.Current.CancellationToken);
        await AssertBareChallengeAsync(basicResponse, "Basic scheme");

        using var emptyBearerRequest = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        emptyBearerRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer");
        using var emptyBearerResponse = await client.SendAsync(emptyBearerRequest, TestContext.Current.CancellationToken);
        await AssertBareChallengeAsync(emptyBearerResponse, "Bearer with an empty value");

        using var malformedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        malformedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not.a.jwt");
        using var malformedResponse = await client.SendAsync(malformedRequest, TestContext.Current.CancellationToken);
        await AssertBareChallengeAsync(malformedResponse, "Bearer not.a.jwt");
    }

    private static async Task AssertBareChallengeAsync(HttpResponseMessage response, string label)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, label);

        response.Headers.WwwAuthenticate.Should().ContainSingle(label);
        var challenge = response.Headers.WwwAuthenticate.Single();
        challenge.Scheme.Should().Be("Bearer", label);
        challenge.Parameter.Should().BeNullOrEmpty(label);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().BeEmpty(label);

        body.Should().NotContain("error", label);
        body.Should().NotContain("IDX", label);
    }

    private static HashSet<string> HeaderNames(HttpResponseMessage response) =>
        response.Headers.Select(h => h.Key)
            .Concat(response.Content.Headers.Select(h => h.Key))
            .Where(name => !string.Equals(name, "Date", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
