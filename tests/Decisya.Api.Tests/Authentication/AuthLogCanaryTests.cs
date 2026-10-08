using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// Issue #121, G3 G4-121-04 (c) and NFR-45: canary scans of the Api's authentication logging. The
/// real logging configuration (<c>appsettings.json</c>, <c>Microsoft.AspNetCore</c> at Warning) with
/// every other category forced to Debug, across accepted, rejected and refused requests that carry
/// canary values in every claim, header and the token itself: none may appear in any record of any
/// category. A second scan removes the configuration thresholds to record what the frameworks would
/// log, and pins that the Warning threshold on <c>Microsoft.AspNetCore</c> stays what keeps their
/// text out. No Docker.
/// </summary>
public class AuthLogCanaryTests : IDisposable
{
    private static readonly object EvidenceLock = new();

    private readonly TestTokenIssuer _issuer = new();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task No_canary_reaches_any_log_record_at_Debug_under_the_real_logging_configuration()
    {
        var provider = new CapturingLoggerProvider();
        await using var factory = ApiTestFactory.Create(
            _issuer,
            environmentName: "Production",
            extraConfiguration: [new("Logging:LogLevel:Default", "Debug")],
            configureLogging: logging => logging.AddProvider(provider));
        using var client = factory.CreateClient();

        var canaries = await DriveHostileRequestsAsync(client);

        AssertNoCanary(provider, canaries);
        provider.Records.Should().NotBeEmpty("Default at Debug should have produced records to scan");
        provider.Records.Where(r => r.EventName == "auth.token.rejected").Should().NotBeEmpty();
    }

    /// <summary>
    /// Evidence, not a gate: with the provider forced to Trace for every category, which framework
    /// categories carry a canary? The T121-09 reliance on the Microsoft.AspNetCore Warning threshold
    /// is recorded in <c>api-log-evidence.txt</c>; the gate above proves the threshold keeps them out.
    /// </summary>
    [Fact]
    public async Task Record_which_framework_categories_would_log_a_canary_without_the_configured_thresholds()
    {
        var provider = new CapturingLoggerProvider();
        await using var factory = ApiTestFactory.Create(
            _issuer,
            environmentName: "Production",
            configureLogging: logging =>
            {
                logging.AddProvider(provider);
                logging.AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace);
            });
        using var client = factory.CreateClient();

        var canaries = await DriveHostileRequestsAsync(client);

        var leaking = provider.Records
            .Where(record => canaries.Any(record.Contains))
            .Select(record => $"{record.Category} [{record.Level}]")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        lock (EvidenceLock)
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "api-log-evidence.txt"),
                $"unfiltered Trace capture, categories whose records carry a canary: {(leaking.Count == 0 ? "(none)" : string.Join("; ", leaking))}{Environment.NewLine}");
        }

        leaking.Should().OnlyContain(
            line => line.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal),
            "only framework categories under Microsoft.AspNetCore (hidden by the Warning threshold) may carry a canary, never a Decisya category");
    }

    [Theory]
    [InlineData("src/Decisya.Api/appsettings.json")]
    [InlineData("src/Decisya.Bff/appsettings.json")]
    public void The_Microsoft_AspNetCore_threshold_that_hides_framework_authentication_text_stays_at_Warning(string relativePath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepoPaths.Find(relativePath.Replace('/', Path.DirectorySeparatorChar))));
        var levels = document.RootElement.GetProperty("Logging").GetProperty("LogLevel");

        levels.GetProperty("Default").GetString().Should().Be("Information");
        levels.GetProperty("Microsoft.AspNetCore").GetString().Should().Be(
            "Warning", "the framework's Information-level authentication logs carry the failure message and exception (T121-09)");
        foreach (var level in levels.EnumerateObject().Where(p => p.Name.StartsWith("Microsoft.AspNetCore.Authentication", StringComparison.Ordinal)))
        {
            level.Value.GetString().Should().BeOneOf("Warning", "Error", "Critical", "None");
        }
    }

    [Theory]
    [InlineData("src/Decisya.Api/appsettings.Development.json")]
    [InlineData("src/Decisya.Bff/appsettings.Development.json")]
    public void The_development_settings_never_lower_an_authentication_category(string relativePath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepoPaths.Find(relativePath.Replace('/', Path.DirectorySeparatorChar))));
        if (!document.RootElement.TryGetProperty("Logging", out var logging) || !logging.TryGetProperty("LogLevel", out var levels))
        {
            return;
        }

        foreach (var level in levels.EnumerateObject().Where(p =>
            p.Name.StartsWith("Microsoft.AspNetCore.Authentication", StringComparison.Ordinal)
            || p.Name.StartsWith("Microsoft.IdentityModel", StringComparison.Ordinal)))
        {
            level.Value.GetString().Should().BeOneOf("Warning", "Error", "Critical", "None");
        }
    }

    // ------------------------------------------------------------------ hostile traffic

    private async Task<List<string>> DriveHostileRequestsAsync(HttpClient client)
    {
        var canaries = new List<string>();
        string Canary(string label)
        {
            var value = Canaries.Unique(label);
            canaries.Add(value);
            return value;
        }

        var sub = Canary("sub");
        var tenant = Canary("tenant");
        var issuer = Canary("iss");
        var audience = Canary("aud");
        var role = Canary("role");
        var acr = Canary("acr");
        var typ = Canary("typ");
        var kid = Canary("kid");

        // 1. A valid token carrying canary claims: accepted.
        var valid = TestTokenIssuer.DefaultClaims(subject: sub, tenantId: null);
        valid["roles"] = new[] { role };
        valid["acr"] = acr;
        valid["canary_extra"] = Canary("extra");
        await SendAsync(client, Sign(valid), canaries);

        // 2. Wrong issuer and audience carrying canaries.
        await SendAsync(client, Sign(TestTokenIssuer.DefaultClaims(subject: sub, issuer: "https://" + issuer + ".test/realms/x")), canaries);
        await SendAsync(client, Sign(TestTokenIssuer.DefaultClaims(subject: sub, audience: audience)), canaries);

        // 3. A malformed tenant claim (the 403 path) and an admin without MFA (the 403 path with its own Warning).
        await SendAsync(client, Sign(TestTokenIssuer.DefaultClaims(subject: sub, tenantId: tenant)), canaries);
        var admin = TestTokenIssuer.DefaultClaims(subject: sub, tenantId: null);
        admin["roles"] = new[] { "platform-admin" };
        admin["acr"] = acr;
        await SendAsync(client, Sign(admin), canaries, "/api/admin/tenants/" + TestTokenIssuer.DevAliceTenantId + "/trial", HttpMethod.Post);

        // 4. An expired token, a token of another type, an HS256 token with a canary kid, a canary in the header.
        await SendAsync(client, Sign(TestTokenIssuer.DefaultClaims(subject: sub, expires: TestTokenIssuer.Now().AddMinutes(-9))), canaries);
        await SendAsync(client, Sign(TestTokenIssuer.DefaultClaims(subject: sub, tokenType: typ)), canaries);
        await SendAsync(
            client, TestTokenIssuer.BuildHs256Token(InvalidTokenCaseCatalog.RandomSecret(), TestTokenIssuer.DefaultClaims(subject: sub), kid), canaries);
        await SendAsync(
            client,
            TestTokenIssuer.IssueToken(
                TestTokenIssuer.DefaultClaims(subject: sub), _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256,
                new Dictionary<string, object>(StringComparer.Ordinal) { ["x_canary"] = Canary("header") }),
            canaries);

        return canaries;
    }

    private string Sign(IDictionary<string, object> claims) =>
        TestTokenIssuer.IssueToken(claims, _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256);

    private static async Task SendAsync(
        HttpClient client, string token, List<string> canaries, string path = "/api/whoami", HttpMethod? method = null)
    {
        canaries.Add(token);
        var segments = token.Split('.');
        if (segments.Length >= 2)
        {
            canaries.Add(segments[0]);
            canaries.Add(segments[1]);
        }

        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static void AssertNoCanary(CapturingLoggerProvider provider, IReadOnlyCollection<string> canaries)
    {
        foreach (var record in provider.Records)
        {
            foreach (var canary in canaries.Where(c => c.Length > 0))
            {
                record.Contains(canary).Should().BeFalse(
                    $"[{record.Category}] ({record.Level}) '{record.Message}' state '{record.StateText}' must carry no token, claim or header value");
            }
        }
    }
}
