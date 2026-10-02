using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Entitlements.Contracts.Admin;
using Decisya.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NodaTime;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// Issue #25, G1 Story 1 ("an anonymous or invalid-token call gets 401, never 403 or a redirect") and the
/// auth-change negative tests: an otherwise perfect platform-admin token that is expired, for another audience
/// or issuer, <c>alg=none</c>, HS256 or signed by an unknown key gets the bare 401 challenge on every admin
/// verb, and no command runs. (The antiforgery-header negative test for the BFF side is
/// <c>Decisya.Bff.Tests.AdminApiAntiforgeryTests</c>.)
/// </summary>
[Trait("Category", "Unit")]
public sealed class AdminTokenRejectionTests : IDisposable
{
    private const string Tenant = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    private readonly TestTokenIssuer _issuer = new();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    public static TheoryData<string> Cases =>
    [
        "expired more than 5 minutes ago",
        "wrong audience",
        "wrong issuer",
        "alg=none",
        "HS256 with a random secret",
        "signed by a second, unknown key",
        "typ is ID, not Bearer",
    ];

    private string Build(string label)
    {
        Dictionary<string, object> Claims(string? audience = null, string? issuer = null, DateTimeOffset? expires = null, string? tokenType = "Bearer")
        {
            var claims = TestTokenIssuer.DefaultClaims(
                subject: "3f2d1c9a-8b7e-4d6c-a5f4-0e1d2c3b4a59", tenantId: null,
                audience: audience ?? TestTokenIssuer.Audience, issuer: issuer ?? TestTokenIssuer.Issuer, expires: expires, tokenType: tokenType);
            claims["roles"] = new[] { "platform-admin" };
            return claims;
        }

        return label switch
        {
            "expired more than 5 minutes ago" => TestTokenIssuer.IssueToken(Claims(expires: TestTokenIssuer.Now().AddMinutes(-6)), _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256),
            "wrong audience" => TestTokenIssuer.IssueToken(Claims(audience: "some-other-api"), _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256),
            "wrong issuer" => TestTokenIssuer.IssueToken(Claims(issuer: "https://wrong-issuer.test/realms/decisya"), _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256),
            "alg=none" => TestTokenIssuer.BuildAlgNoneToken(Claims()),
            "HS256 with a random secret" => TestTokenIssuer.BuildHs256Token(InvalidTokenCaseCatalog.RandomSecret(), Claims()),
            "signed by a second, unknown key" => TestTokenIssuer.IssueToken(Claims(), _issuer.OtherRsaSigningKey, SecurityAlgorithms.RsaSha256),
            "typ is ID, not Bearer" => TestTokenIssuer.IssueToken(Claims(tokenType: "ID"), _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256),
            _ => throw new ArgumentOutOfRangeException(nameof(label), label, null),
        };
    }

    [Theory]
    [MemberData(nameof(Cases), DisableDiscoveryEnumeration = true)]
    public async Task An_invalid_admin_token_gets_the_bare_401_on_every_verb_and_no_command_runs(string label)
    {
        var calls = 0;
        await using var factory = ApiTestFactory.Create(
            _issuer,
            environmentName: "Production",
            configureServices: services => services.AddScoped<IEntitlementAdminCommands>(_ => new CountingCommands(() => Interlocked.Increment(ref calls))));
        using var client = factory.CreateClient();
        var token = Build(label);

        foreach (var (method, path) in new[]
        {
            ("POST", $"/api/admin/tenants/{Tenant}/trial"),
            ("PUT", $"/api/admin/tenants/{Tenant}/overrides/forecasting.scenarios"),
            ("DELETE", $"/api/admin/tenants/{Tenant}/overrides/forecasting.scenarios"),
        })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (method == "PUT")
            {
                request.Content = new StringContent("""{"reason":"p"}""", Encoding.UTF8, "application/json");
            }

            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{label}: {method}");
            response.Headers.WwwAuthenticate.Should().ContainSingle().Which.Scheme.Should().Be("Bearer");
            (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().BeEmpty($"{label}: no detail");
        }

        calls.Should().Be(0);
    }

    private sealed class CountingCommands(Action onCall) : IEntitlementAdminCommands
    {
        public Task<EntitlementAdminResult> StartTrialAsync(TenantId tenantId, CancellationToken cancellationToken = default) => Hit();

        public Task<EntitlementAdminResult> GrantOverrideAsync(
            TenantId tenantId, FeatureKey feature, string reason, Instant? expiresAt, CancellationToken cancellationToken = default) => Hit();

        public Task<EntitlementAdminResult> RevokeOverrideAsync(
            TenantId tenantId, FeatureKey feature, CancellationToken cancellationToken = default) => Hit();

        private Task<EntitlementAdminResult> Hit()
        {
            onCall();
            return Task.FromResult(EntitlementAdminResult.Succeeded);
        }
    }
}
