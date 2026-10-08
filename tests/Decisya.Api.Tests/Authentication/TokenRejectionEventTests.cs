using System.Net;
using System.Net.Http.Headers;
using Decisya.Api.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// Issue #121, G2 D4 and G3 G4-121-04 (a): every rejected bearer token is exactly one
/// <c>auth.token.rejected</c> Warning with a reason from the closed list, including the <c>typ</c>
/// rejection that raises no <c>OnAuthenticationFailed</c> (<c>bad_type</c>, T121-08). A missing token
/// logs no event. The 401 and its bare challenge are unchanged. No Docker.
/// </summary>
public class TokenRejectionEventTests : IDisposable
{
    private const string EventName = "auth.token.rejected";

    private readonly TestTokenIssuer _issuer = new();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The expected reason per row of the shared invalid-token catalogue.</summary>
    private static readonly Dictionary<string, string> ExpectedReasons = new(StringComparer.Ordinal)
    {
        ["alg=none, empty signature"] = TokenRejectionReason.BadAlgorithm,
        ["alg=None, empty signature"] = TokenRejectionReason.BadAlgorithm,
        ["alg=NONE, empty signature"] = TokenRejectionReason.BadAlgorithm,
        ["alg=none, non-empty signature"] = TokenRejectionReason.BadAlgorithm,
        ["HS256, random secret"] = TokenRejectionReason.BadAlgorithm,
        ["HS256, realm RSA public key as PEM text"] = TokenRejectionReason.BadAlgorithm,
        ["HS256, realm RSA public key as DER bytes"] = TokenRejectionReason.BadAlgorithm,
        ["PS256, genuine key, not on the allow-list"] = TokenRejectionReason.BadAlgorithm,
        ["RS512, genuine key, not on the allow-list"] = TokenRejectionReason.BadAlgorithm,
        ["embedded attacker jwk header"] = TokenRejectionReason.BadSignature,
        ["unknown kid / signed by a second key"] = TokenRejectionReason.BadSignature,
        ["wrong audience"] = TokenRejectionReason.BadAudience,
        ["wrong issuer"] = TokenRejectionReason.BadIssuer,
        ["expired more than 5 minutes ago"] = TokenRejectionReason.Expired,
        ["no exp claim"] = TokenRejectionReason.Other,
        ["nbf far in the future"] = TokenRejectionReason.Other,
        ["typ is ID, not Bearer"] = TokenRejectionReason.BadType,
        ["no typ claim"] = TokenRejectionReason.BadType,
    };

    [Fact]
    public void The_catalogue_and_the_expected_reasons_cover_the_same_rows_and_only_closed_reasons()
    {
        InvalidTokenCaseCatalog.Cases().Select(c => c.Label).Should().BeEquivalentTo(ExpectedReasons.Keys);
        ExpectedReasons.Values.Should().OnlyContain(reason => TokenRejectionReason.All.Contains(reason));
        TokenRejectionReason.All.Should().BeEquivalentTo(
            ["expired", "bad_signature", "bad_issuer", "bad_audience", "bad_algorithm", "malformed", "bad_type", "other"]);
    }

    [Fact]
    public async Task Each_invalid_token_row_logs_exactly_one_warning_with_its_closed_reason_and_stays_a_bare_401()
    {
        var provider = new CapturingLoggerProvider();
        await using var factory = ApiTestFactory.Create(_issuer, configureLogging: logging => logging.AddProvider(provider));
        using var client = factory.CreateClient();

        foreach (var (label, buildToken) in InvalidTokenCaseCatalog.Cases())
        {
            provider.Records.Clear();
            var token = buildToken(_issuer);

            using var response = await GetAsync(client, token);

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, label);
            response.Headers.WwwAuthenticate.Single().ToString().Should().Be("Bearer", label);
            var events = provider.Records.Where(r => r.EventName == EventName).ToList();
            events.Should().ContainSingle(label);
            events[0].Level.Should().Be(LogLevel.Warning, label);
            events[0].StateText.Should().Contain($"Reason={ExpectedReasons[label]}", label);
            events[0].Contains(token).Should().BeFalse(label);
            events[0].Contains(token.Split('.')[0]).Should().BeFalse($"{label}: no header segment");
        }
    }

    [Theory]
    [InlineData("Bearer not-a-jwt")]
    [InlineData("Bearer a.b.c")]
    [InlineData("Bearer ....")]
    [InlineData("Bearer eyJhbGciOiJSUzI1NiJ9.e30")]
    public async Task A_malformed_token_logs_the_malformed_reason(string authorization)
    {
        var provider = new CapturingLoggerProvider();
        await using var factory = ApiTestFactory.Create(_issuer, configureLogging: logging => logging.AddProvider(provider));
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var events = provider.Records.Where(r => r.EventName == EventName).ToList();
        events.Should().ContainSingle();
        events[0].StateText.Should().Contain("Reason=malformed");
    }

    /// <summary>G4-121-04 (a): an ID-typed token and a refresh-typed token each log exactly one bad_type event.</summary>
    [Theory]
    [InlineData("ID")]
    [InlineData("Refresh")]
    [InlineData("Offline")]
    [InlineData("bearer")]
    [InlineData("")]
    public async Task A_token_of_another_type_logs_exactly_one_bad_type_event(string tokenType)
    {
        var provider = new CapturingLoggerProvider();
        await using var factory = ApiTestFactory.Create(_issuer, configureLogging: logging => logging.AddProvider(provider));
        using var client = factory.CreateClient();
        var token = TestTokenIssuer.IssueToken(
            TestTokenIssuer.DefaultClaims(tokenType: tokenType), _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256);

        using var response = await GetAsync(client, token);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var events = provider.Records.Where(r => r.EventName == EventName).ToList();
        events.Should().ContainSingle();
        events[0].StateText.Should().Contain("Reason=bad_type");
    }

    [Fact]
    public async Task A_missing_token_and_a_non_bearer_scheme_and_a_valid_token_log_no_rejection_event()
    {
        var provider = new CapturingLoggerProvider();
        await using var factory = ApiTestFactory.Create(_issuer, configureLogging: logging => logging.AddProvider(provider));
        using var client = factory.CreateClient();

        using (var anonymous = await client.GetAsync("/api/whoami", TestContext.Current.CancellationToken))
        {
            anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        using (var basic = new HttpRequestMessage(HttpMethod.Get, "/api/whoami"))
        {
            basic.Headers.Authorization = new AuthenticationHeaderValue("Basic", "Zm9vOmJhcg==");
            using var response = await client.SendAsync(basic, TestContext.Current.CancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        using (var valid = await GetAsync(client, _issuer.IssueValidAccessToken()))
        {
            valid.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        provider.Records.Where(r => r.EventName == EventName).Should().BeEmpty();
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
