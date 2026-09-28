using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// G4-20-05 / Story 4 (T-09): an unhandled exception never reaches the client as detail, in
/// every environment, regardless of the requested <c>Accept</c> type — and the token itself
/// (not only the canary) never appears in the response.
/// </summary>
public class ExceptionHandlingTests : IDisposable
{
    private readonly TestTokenIssuer _issuer = new();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("Development", "application/json")]
    [InlineData("Development", "text/html")]
    [InlineData("Production", "application/json")]
    [InlineData("Production", "text/html")]
    public async Task An_unhandled_exception_never_reaches_the_client_as_detail(string environmentName, string accept)
    {
        var canary = Canaries.Unique("exception-detail");
        var capturingProvider = new CapturingLoggerProvider();
        await using var factory = ApiTestFactory.Create(
            _issuer,
            environmentName: environmentName,
            testEndpointsCanary: canary,
            configureLogging: logging => logging.AddProvider(capturingProvider));
        using var client = factory.CreateClient();

        var token = _issuer.IssueValidAccessToken();
        using var request = new HttpRequestMessage(HttpMethod.Get, TestOnlyEndpointsStartupFilter.ThrowPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain(canary);
        body.Should().NotContain(token);
        body.Should().NotContain("Authorization");
        body.Should().NotContain("dev-alice");

        capturingProvider.Records.Should().Contain(record => record.Contains(canary), "the exception's detail should still reach the log");
    }
}
