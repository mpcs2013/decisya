using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Headers;
using Decisya.Api.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// G4-20-02 (T-05, T-12, T-13): the key source cannot be downgraded or redirected.
/// </summary>
/// <remarks>
/// The Development-only-relaxation rows validate <see cref="ApiJwtOptions"/> and
/// <see cref="ApiJwtOptionsEnvironmentValidator"/> directly, not through a
/// <c>WebApplicationFactory&lt;Program&gt;</c> host. Found empirically (#20 flake): a host built
/// from a top-level-statements entry point (<c>Decisya.Api</c>'s <c>Program.cs</c>) runs through
/// <c>Microsoft.Extensions.Hosting.HostFactoryResolver</c>'s diagnostic-listener interception,
/// which is not safe against a *concurrent host-start failure* on another thread: under full
/// parallel test execution, a small fraction of runs saw <c>Record.Exception(() =&gt;
/// factory.Server)</c> return a bare <c>ObjectDisposedException: Cannot access a disposed
/// object. Object name: 'IServiceProvider'.</c> instead of the expected
/// <see cref="OptionsValidationException"/> — a different, concurrently starting-and-failing
/// host's teardown reaching into this one. Confirmed by instrumenting the failure to dump the
/// full exception tree, and by reproducing it running the whole assembly's own test binary in a
/// loop; a class-local lock serializing only this class's own host builds did not make it go
/// away (2 failures in 60 runs), because the interference comes from *other* test classes'
/// concurrently-starting (successful) hosts too, not just this class's own. Testing the
/// validation logic directly removes the host, and with it the race, entirely — the DI wiring
/// itself (<c>AddOptions&lt;ApiJwtOptions&gt;().Bind(...).ValidateDataAnnotations().ValidateOnStart()</c>,
/// <c>ConfigureOptions&lt;JwtBearerOptionsSetup&gt;</c>) is still exercised by every other test in
/// this file and in <c>JwtBearerOptionsPinnedTests</c>, which build a real, *successful* host —
/// a combination that never reproduced the race in a 320-instance concurrent stress test.
/// </remarks>
public class ApiJwtOptionsTests : IDisposable
{
    private readonly TestTokenIssuer _issuer = new();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [MemberData(nameof(InvalidOutsideDevelopmentCases))]
    public void A_Development_only_relaxation_fails_validation_in_Production(string overriddenKey, string? overriddenValue)
    {
        var options = BuildOptions(overriddenKey, overriddenValue);

        var (failed, messages) = Validate(options, Environments.Production);

        failed.Should().BeTrue("startup should fail with an options validation failure, not some other error");

        // Only the Authority row carries an arbitrary configured value that could leak (CLAUDE.md:
        // "the error message names the key, never the value"); RequireHttpsMetadata's value is
        // always the literal word "true"/"false", which legitimately appears in the rule's own
        // fixed English description ("...may be false only in Development.") — that word is not a
        // leaked value.
        if (overriddenValue is not null && overriddenKey == "Api:Jwt:Authority")
        {
            string.Join(' ', messages).Should().NotContain(overriddenValue);
        }
    }

    [Fact]
    public void A_relative_Authority_fails_validation_in_every_environment()
    {
        var options = new ApiJwtOptions { Authority = "not-a-uri" };

        var (failed, _) = Validate(options, Environments.Development);

        failed.Should().BeTrue("Api:Jwt:Authority must be an absolute URI in every environment, Development included");
    }

    [Fact]
    public void The_same_configuration_that_fails_in_Production_passes_in_Development()
    {
        var options = new ApiJwtOptions { Authority = "https://good.test/realms/decisya" };

        var (failed, _) = Validate(options, Environments.Development);

        failed.Should().BeFalse();
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
    /// G3 (G4-20-02): a metadata outage gives the same bare 401 as every other rejection. G6
    /// review (F1): the earlier version of this test used a hand-rolled
    /// <c>IConfigurationManager&lt;T&gt;</c>-only double, which does not reproduce what
    /// <c>JwtBearerHandler</c> actually does against a real, unreachable Keycloak — the handler
    /// only catches the fetch failure (IDX10261) and returns a bare 401 when the configured
    /// manager is a real <c>BaseConfigurationManager</c>; with the double the exception
    /// propagated instead and <c>UseExceptionHandler</c> produced a generic 500. This now uses
    /// <see cref="UnreachableConfigurationManager"/>, a real
    /// <c>Microsoft.IdentityModel.Protocols.ConfigurationManager&lt;OpenIdConnectConfiguration&gt;</c>
    /// pointed at an address nothing listens on, confirmed against the built API to give 401,
    /// an empty body and a bare <c>WWW-Authenticate: Bearer</c>.
    /// </summary>
    [Fact]
    public async Task Metadata_unavailable_gives_a_bare_401_with_no_detail_leaked()
    {
        await using var factory = ApiTestFactory.Create(
            _issuer, configurationManager: UnreachableConfigurationManager.Create());
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _issuer.IssueValidAccessToken());

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Should().ContainSingle();
        response.Headers.WwwAuthenticate.Single().Scheme.Should().Be("Bearer");
        response.Headers.WwwAuthenticate.Single().Parameter.Should().BeNullOrEmpty();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().BeEmpty();
        body.Should().NotContain(TestTokenIssuer.Issuer);
        body.Should().NotContain(UnreachableConfigurationManager.Address);
    }

    public static IEnumerable<object?[]> InvalidOutsideDevelopmentCases()
    {
        yield return new object?[] { "Api:Jwt:Authority", null };
        yield return new object?[] { "Api:Jwt:Authority", "relative/path" };
        yield return new object?[] { "Api:Jwt:Authority", "http://good.test/realms/decisya" };
        yield return new object?[] { "Api:Jwt:RequireHttpsMetadata", "false" };
    }

    private static ApiJwtOptions BuildOptions(string overriddenKey, string? overriddenValue) =>
        overriddenKey switch
        {
            "Api:Jwt:Authority" => new ApiJwtOptions { Authority = overriddenValue },
            "Api:Jwt:RequireHttpsMetadata" => new ApiJwtOptions
            {
                Authority = "https://good.test/realms/decisya",
                RequireHttpsMetadata = bool.Parse(overriddenValue!),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(overriddenKey), overriddenKey, "Unknown override key."),
        };

    /// <summary>
    /// Runs the same two validators <c>AddOptions&lt;ApiJwtOptions&gt;().ValidateDataAnnotations()</c>
    /// and the DI-registered <see cref="ApiJwtOptionsEnvironmentValidator"/> would, without a host.
    /// </summary>
    private static (bool Failed, IReadOnlyList<string> Messages) Validate(ApiJwtOptions options, string environmentName)
    {
        var messages = new List<string>();

        var dataAnnotationResults = new List<ValidationResult>();
        var dataAnnotationsValid = Validator.TryValidateObject(
            options, new ValidationContext(options), dataAnnotationResults, validateAllProperties: true);
        messages.AddRange(dataAnnotationResults.Select(result => result.ErrorMessage ?? string.Empty));

        var environmentResult = new ApiJwtOptionsEnvironmentValidator(new FakeHostEnvironment(environmentName))
            .Validate(null, options);
        if (environmentResult.Failed)
        {
            messages.AddRange(environmentResult.Failures);
        }

        return (!dataAnnotationsValid || environmentResult.Failed, messages);
    }

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Decisya.Api.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
