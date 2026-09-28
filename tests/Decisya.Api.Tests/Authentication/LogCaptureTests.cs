using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// G4-20-05's log-capture scan (T-09, T-11): across a 200, every G4-20-01 negative row, and
/// both 403 cases, no log record's message, structured state or exception text may carry a
/// token (raw or its payload segment), a <c>sub</c>/<c>tenant_id</c> canary, or the HS256
/// secret / PEM text used in the confusion rows. Runs at <see cref="LogLevel.Trace"/> so a
/// level setting elsewhere can never hide a regression.
/// </summary>
public class LogCaptureTests : IDisposable
{
    private readonly TestTokenIssuer _issuer = new();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task No_token_or_claim_reaches_any_log()
    {
        var capturingProvider = new CapturingLoggerProvider();
        var canarySubject = Canaries.Unique("sub");
        await using var factory = ApiTestFactory.Create(
            _issuer,
            configureLogging: logging =>
            {
                logging.AddProvider(capturingProvider);
                logging.AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace);
            });
        using var client = factory.CreateClient();

        var sensitiveValues = new List<string> { canarySubject };

        var validToken = _issuer.IssueValidAccessToken(subject: canarySubject);
        sensitiveValues.Add(validToken);
        await SendAsync(client, validToken);

        foreach (var (_, buildToken) in InvalidTokenCaseCatalog.Cases())
        {
            var token = buildToken(_issuer);
            sensitiveValues.Add(token);
            await SendAsync(client, token);
        }

        var noSubToken = TestTokenIssuer.IssueToken(
            TestTokenIssuer.DefaultClaims(subject: null), _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256);
        sensitiveValues.Add(noSubToken);
        await SendAsync(client, noSubToken);

        var duplicateTenantClaims = TestTokenIssuer.DefaultClaims(tenantId: null);
        duplicateTenantClaims["tenant_id"] = new[] { "tenant-a", "tenant-b" };
        var duplicateTenantToken = TestTokenIssuer.IssueToken(duplicateTenantClaims, _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256);
        sensitiveValues.Add(duplicateTenantToken);
        await SendAsync(client, duplicateTenantToken);

        // Also scan the JWT payload segment of each token: a leak could echo just the decoded
        // claims, not the raw encoded token (mirrors Decisya.Bff.Tests' LogScanTests technique).
        var payloadSegments = sensitiveValues
            .Where(value => value.Count(c => c == '.') >= 2)
            .Select(value => value.Split('.')[1])
            .Where(segment => segment.Length > 0)
            .ToList();
        sensitiveValues.AddRange(payloadSegments);
        sensitiveValues = sensitiveValues.Distinct().ToList();

        var records = capturingProvider.Records.ToList();
        records.Should().NotBeEmpty("forcing the capturing provider to Trace should have produced log records to scan");

        foreach (var record in records)
        {
            foreach (var value in sensitiveValues)
            {
                record.Contains(value).Should().BeFalse(
                    $"[{record.Category}] '{record.Message}' (state: '{record.StateText}') must not carry a token or claim value");
            }
        }
    }

    private static async Task SendAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
