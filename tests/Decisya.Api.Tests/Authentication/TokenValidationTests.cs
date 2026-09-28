using System.Net;
using System.Net.Http.Headers;
using Microsoft.IdentityModel.Tokens;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// G4-20-01 (T-01..T-04; Story 2, NFR-26, NFR-27): only a genuine, current, RS256/ES256 access
/// token for <c>decisya-api</c> authenticates. Every negative row gives 401, and the positive
/// rows in the same class prove the allow-list is not simply empty.
/// </summary>
public class TokenValidationTests : IDisposable
{
    private readonly TestTokenIssuer _issuer = new();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [MemberData(nameof(InvalidTokenCaseCatalog.AsTheoryData), MemberType = typeof(InvalidTokenCaseCatalog))]
    public async Task Invalid_tokens_are_rejected(string label, Func<TestTokenIssuer, string> buildToken)
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", buildToken(_issuer));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, label);
    }

    [Fact]
    public async Task No_Authorization_header_at_all_is_rejected()
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/whoami", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_non_Bearer_scheme_is_rejected()
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", "dXNlcjpwYXNz");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not.a.jwt")]
    public async Task An_empty_or_malformed_bearer_value_is_rejected(string value)
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {value}".TrimEnd());

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_genuine_RS256_token_authenticates()
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _issuer.IssueValidAccessToken());

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_genuine_ES256_token_authenticates()
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        using var client = factory.CreateClient();
        var token = TestTokenIssuer.IssueToken(TestTokenIssuer.DefaultClaims(), _issuer.EcSigningKey, SecurityAlgorithms.EcdsaSha256);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// NFR-27's behavioural proof — deliberately not at the exact 59 s/61 s boundary. Found
    /// empirically (#20 flake): under full parallel test execution, the ~1 s margin between
    /// minting a token and the server validating it could itself eat into a 1 s skew margin,
    /// intermittently failing this test for a timing reason, not a product defect. The *exact*
    /// value of <c>ClockSkew</c> (60 s) is pinned precisely, with no wall-clock dependency, by
    /// <see cref="JwtBearerOptionsPinnedTests.Validation_parameters_are_pinned"/>; this test only
    /// needs to prove "comfortably inside is accepted, comfortably outside is rejected", so it
    /// uses 45 s/75 s — a 15 s margin either side of the 60 s boundary — instead of shaving the
    /// boundary itself. Do not narrow these back toward 60 s; that reintroduces the flake this
    /// margin exists to avoid.
    /// </summary>
    [Fact]
    public async Task A_token_45_seconds_past_expiry_is_accepted_and_75_seconds_past_expiry_is_rejected()
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        using var client = factory.CreateClient();

        var withinSkewToken = TestTokenIssuer.IssueToken(
            TestTokenIssuer.DefaultClaims(expires: TestTokenIssuer.Now().AddSeconds(-45)), _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256);
        using var withinSkewRequest = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        withinSkewRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", withinSkewToken);
        using var withinSkewResponse = await client.SendAsync(withinSkewRequest, TestContext.Current.CancellationToken);
        withinSkewResponse.StatusCode.Should().Be(HttpStatusCode.OK, "45 s past exp is comfortably inside the 60 s clock skew");

        var beyondSkewToken = TestTokenIssuer.IssueToken(
            TestTokenIssuer.DefaultClaims(expires: TestTokenIssuer.Now().AddSeconds(-75)), _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256);
        using var beyondSkewRequest = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        beyondSkewRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", beyondSkewToken);
        using var beyondSkewResponse = await client.SendAsync(beyondSkewRequest, TestContext.Current.CancellationToken);
        beyondSkewResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "75 s past exp is comfortably outside the 60 s clock skew");
    }
}
