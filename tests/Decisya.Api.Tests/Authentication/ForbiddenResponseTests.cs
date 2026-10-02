using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// The two 403 cases G3 names, plus S-2 (T-14): a valid token whose <c>sub</c> or
/// <c>tenant_id</c> claims cannot be trusted as a single identity fails closed with no claim
/// value ever reaching the response.
/// </summary>
public class ForbiddenResponseTests : IDisposable
{
    private const string CanaryTenantA = "canary-tenant-aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string CanaryTenantB = "canary-tenant-bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    private readonly TestTokenIssuer _issuer = new();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Issue #21, G2: since <c>CallerContextMiddleware</c> now runs before routing, a token
    /// missing <c>sub</c> is rejected there (a null <c>CallerIdentity</c>) rather than by the
    /// fallback authorization policy's own <c>RequireClaim("sub")</c> — which never runs, since
    /// the request is short-circuited first. The body is therefore the same generic
    /// ProblemDetails shape every other untrustworthy-identity case in this file gets, not the
    /// framework's bare empty-body Forbid this test pinned before #21 (#25 G4-25-03 has since
    /// unified the <c>Tenancy.Owner</c> policy 403 with this shape, closing S-2).
    /// </summary>
    [Fact]
    public async Task A_valid_token_without_a_sub_claim_gives_a_generic_403_problem_details_with_no_challenge_header()
    {
        var token = TestTokenIssuer.IssueToken(
            TestTokenIssuer.DefaultClaims(subject: null), _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256);

        using var response = await SendAsync(token);

        response.Headers.WwwAuthenticate.Should().BeEmpty();
        await AssertGenericForbiddenProblemAsync(response, []);
    }

    /// <summary>
    /// S-2's duplicate-<c>sub</c> row, proven directly against <c>CallerIdentity.From</c>
    /// rather than through a full HTTP round trip. Found empirically (two routes tried and
    /// rejected): a JSON array value for <c>sub</c> — the shape that gives <c>aud</c> multiple
    /// claims — makes <c>JsonWebToken</c> throw while parsing (IDX11020/IDX14101), because
    /// <c>sub</c> is one of the JWT-registered claims it insists on reading as a single string;
    /// two literal duplicate <c>"sub"</c> JSON keys parse, but <c>JsonWebToken</c>'s own reader
    /// keeps only the last value (ordinary last-key-wins dictionary construction), so the
    /// resulting <see cref="ClaimsPrincipal"/> never actually carries two <c>sub</c> claims
    /// either way. Two <c>sub</c> claims on an authenticated principal is therefore not
    /// reachable through any token this pipeline's own parser can produce — but
    /// <see cref="Decisya.Api.Authentication.CallerIdentity.From"/> must still fail closed if a
    /// future claims transformation, or a different token handler, ever produces one, so this
    /// tests the method directly.
    /// </summary>
    [Fact]
    public void Two_sub_claims_on_a_principal_make_CallerIdentity_return_null()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim("sub", "canary-sub-a"),
            new Claim("sub", "canary-sub-b"),
            new Claim("tenant_id", TestTokenIssuer.DevAliceTenantId),
        ]);

        var result = Decisya.Api.Authentication.CallerIdentity.From(new ClaimsPrincipal(identity));

        result.Should().BeNull();
    }

    [Fact]
    public async Task Two_tenant_id_claims_give_a_generic_403_problem_details_with_no_claim_value()
    {
        // A JSON array value (unlike a literal duplicate "tenant_id" key, which the parser
        // deduplicates to its last value) is the one shape that reliably reaches the
        // ClaimsPrincipal as two claims for a claim name outside the JWT-registered set.
        var claims = TestTokenIssuer.DefaultClaims(tenantId: null);
        claims["tenant_id"] = new[] { CanaryTenantA, CanaryTenantB };
        var token = TestTokenIssuer.IssueToken(claims, _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256);

        using var response = await SendAsync(token);

        await AssertGenericForbiddenProblemAsync(response, [CanaryTenantA, CanaryTenantB]);
    }

    [Fact]
    public async Task A_blank_sub_gives_a_generic_403_problem_details()
    {
        var token = TestTokenIssuer.IssueToken(
            TestTokenIssuer.DefaultClaims(subject: "   "), _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256);

        using var response = await SendAsync(token);

        await AssertGenericForbiddenProblemAsync(response, []);
    }

    [Fact]
    public async Task A_blank_tenant_id_gives_a_generic_403_problem_details()
    {
        var token = TestTokenIssuer.IssueToken(
            TestTokenIssuer.DefaultClaims(tenantId: "   "), _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256);

        using var response = await SendAsync(token);

        await AssertGenericForbiddenProblemAsync(response, []);
    }

    private async Task<HttpResponseMessage> SendAsync(string token)
    {
        await using var factory = ApiTestFactory.Create(_issuer);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task AssertGenericForbiddenProblemAsync(HttpResponseMessage response, string[] excludedCanaries)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("\"status\"");
        foreach (var canary in excludedCanaries)
        {
            body.Should().NotContain(canary);
        }
    }
}
