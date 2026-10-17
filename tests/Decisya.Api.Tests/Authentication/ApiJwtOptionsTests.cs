using System.Net;
using System.Net.Http.Headers;
using Decisya.Api.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Ostium.Authentication.Jwt.Testing;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// G4-20-02 (T-05, T-12, T-13): the key source cannot be downgraded or redirected. Since #164 the
/// validation lives in <c>Ostium.Authentication.Jwt</c>; these rows stay in Decisya and go through
/// Decisya's own <c>AddDecisyaApiAuthentication</c> (G3 M-2), because they are the compensating
/// control for the missing 0.1.1 report.
/// </summary>
/// <remarks>
/// The Production start-up rows build a <c>WebApplication</c> from in-memory configuration only
/// and call <see cref="IStartupValidator"/> directly, with no <c>WebApplicationFactory&lt;Program&gt;</c>
/// host. A host built from the top-level-statements entry point runs through
/// <c>HostFactoryResolver</c>'s diagnostic-listener interception, which is not safe against a
/// concurrent host-start failure on another thread (an unrelated, concurrently failing host's
/// teardown can surface here as an <see cref="ObjectDisposedException"/>). Validating without that
/// host removes the race. The Development rows build real, successful hosts, which never
/// reproduced it.
/// </remarks>
public class ApiJwtOptionsTests : IDisposable
{
    private readonly TestTokenIssuer _issuer = DecisyaTestTokens.Create();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [MemberData(nameof(InvalidOutsideDevelopmentCases))]
    public void A_Development_only_relaxation_fails_validation_in_Production(string? authority, string? requireHttpsMetadata)
    {
        var failures = StartupFailures(Environments.Production, authority, requireHttpsMetadata);

        failures.Should().NotBeEmpty("start-up should fail with an options validation failure, not some other error");

        // Only the Authority rows carry an arbitrary configured value that could leak (CLAUDE.md:
        // "the error message names the key, never the value"); RequireHttpsMetadata's value is
        // the literal word "true"/"false", which legitimately appears in a rule's own fixed text.
        if (authority is not null)
        {
            string.Join(' ', failures).Should().NotContain(authority);
        }
    }

    [Fact]
    public void A_relative_Authority_fails_validation_in_every_environment()
    {
        var failures = StartupFailures(Environments.Development, "not-a-uri", requireHttpsMetadata: null);

        failures.Should().NotBeEmpty("Api:Jwt:Authority must be an absolute URI in every environment, Development included");
    }

    [Fact]
    public void The_same_configuration_that_fails_in_Production_passes_in_Development()
    {
        var failures = StartupFailures(Environments.Development, "https://good.test/realms/decisya", requireHttpsMetadata: null);

        failures.Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://localhost:8080/realms/decisya", false)]
    [InlineData("https://127.0.0.1:8080/realms/decisya", false)]
    [InlineData("https://localhost.evil.test/realms/decisya", true)]
    [InlineData("https://localhost@evil.test/realms/decisya", true)]
    [InlineData("https://127.0.0.1.nip.io/realms/decisya", true)]
    [InlineData("https://keycloak/realms/decisya", true)]
    public async Task Https_metadata_relaxed_only_for_loopback_in_Development(string authority, bool expectedRequireHttpsMetadata)
    {
        await using var factory = ApiTestFactory.Create(
            _issuer,
            extraConfiguration:
            [
                new("Api:Jwt:Authority", authority),
                new("Api:Jwt:RequireHttpsMetadata", "false"),
            ]);

        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        options.RequireHttpsMetadata.Should().Be(expectedRequireHttpsMetadata);
    }

    [Fact]
    public async Task With_the_flag_absent_RequireHttpsMetadata_stays_true_for_every_host()
    {
        await using var factory = ApiTestFactory.Create(
            _issuer, extraConfiguration: [new("Api:Jwt:Authority", "https://localhost:8080/realms/decisya")]);

        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        options.RequireHttpsMetadata.Should().BeTrue();
    }

    /// <summary>
    /// G3 (G4-20-02): a metadata outage gives the same bare 401 as every other rejection. The
    /// double must be a real <c>BaseConfigurationManager</c> (<see cref="UnreachableConfigurationManager"/>):
    /// the handler only catches the fetch failure (IDX10261) and returns a bare 401 for one.
    /// </summary>
    [Fact]
    public async Task Metadata_unavailable_gives_a_bare_401_with_no_detail_leaked()
    {
        await using var factory = ApiTestFactory.Create(
            _issuer, configurationManager: UnreachableConfigurationManager.Create());
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", _issuer.IssueAccessToken("dev-alice", DecisyaTestTokens.Tenant(DecisyaTestTokens.DevAliceTenantId)));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Should().ContainSingle();
        response.Headers.WwwAuthenticate.Single().Scheme.Should().Be("Bearer");
        response.Headers.WwwAuthenticate.Single().Parameter.Should().BeNullOrEmpty();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().BeEmpty();
        body.Should().NotContain(DecisyaTestTokens.Issuer);
    }

    public static IEnumerable<object?[]> InvalidOutsideDevelopmentCases()
    {
        yield return new object?[] { null, null };
        yield return new object?[] { "relative/path", null };
        yield return new object?[] { "http://good.test/realms/decisya", null };
        yield return new object?[] { "https://good.test/realms/decisya", "false" };
    }

    /// <summary>
    /// Builds Decisya's real registration from in-memory configuration only (the audience and the
    /// event settings are always set, so only the Authority rows can fail) and runs the
    /// <c>ValidateOnStart</c> validators without starting a host. Returns every failure message.
    /// </summary>
    private static List<string> StartupFailures(string environmentName, string? authority, string? requireHttpsMetadata)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Api:Jwt:Audience"] = DecisyaTestTokens.Audience,
            ["Api:Jwt:Events:TokenRejected:Name"] = "auth.token.rejected",
            ["Api:Jwt:Events:TokenRejected:Id"] = "2003",
        };
        if (authority is not null)
        {
            values["Api:Jwt:Authority"] = authority;
        }

        if (requireHttpsMetadata is not null)
        {
            values["Api:Jwt:RequireHttpsMetadata"] = requireHttpsMetadata;
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environmentName });
        builder.Configuration.AddInMemoryCollection(values);

        builder.AddDecisyaApiAuthentication();

        using var app = builder.Build();
        var validator = app.Services.GetRequiredService<IStartupValidator>();

        return Flatten(Record.Exception(validator.Validate));
    }

    private static List<string> Flatten(Exception? exception) => exception switch
    {
        null => [],
        AggregateException aggregate => aggregate.InnerExceptions.SelectMany(Flatten).ToList(),
        OptionsValidationException options => options.Failures.ToList(),
        _ => [exception.Message],
    };
}
