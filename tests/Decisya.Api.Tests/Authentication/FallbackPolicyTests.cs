using System.Net;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// G4-20-04 (T-07; Stories 1, 5): deny by default. Every endpoint, and every unmatched path,
/// requires an authenticated caller with a <c>sub</c> claim — an anonymous caller gets 401,
/// never 404 or 405 (no route discovery).
/// </summary>
public class FallbackPolicyTests : IDisposable
{
    private readonly TestTokenIssuer _issuer = new();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("GET", "/api/whoami")]
    [InlineData("POST", "/api/whoami")]
    [InlineData("GET", "/api/does-not-exist")]
    [InlineData("GET", "/")]
    [InlineData("GET", "/health")]
    [InlineData("GET", "/alive")]
    public async Task An_anonymous_caller_gets_401_never_404_or_405_in_Production(string method, string path)
    {
        await using var factory = ApiTestFactory.Create(_issuer, environmentName: "Production");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{method} {path}");
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task Health_and_alive_stay_anonymous_in_Development(string path)
    {
        await using var factory = ApiTestFactory.Create(_issuer, environmentName: "Development");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, path);
    }

    [Fact]
    public async Task An_anonymous_caller_gets_401_for_an_unmatched_path_in_Development_too()
    {
        await using var factory = ApiTestFactory.Create(_issuer, environmentName: "Development");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/does-not-exist", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
