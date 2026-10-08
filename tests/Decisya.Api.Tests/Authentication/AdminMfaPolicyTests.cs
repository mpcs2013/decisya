using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Decisya.Api.Authentication;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Entitlements.Contracts.Admin;
using Decisya.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NodaTime;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// Issue #121, G3 G4-121-01 (a) and (d): the MFA proof on <c>/api/admin</c>. Exactly one string
/// <c>acr</c> claim equal to "2" from the validated token; the policy combines with
/// <c>Admin.PlatformAdmin</c>; a platform admin without the proof gets the generic 403 and one
/// Warning; and <c>Authentication:RequireAdminMfa</c> fails closed outside Development. No Docker.
/// </summary>
public class AdminMfaPolicyTests : IDisposable
{
    private const string EventName = "auth.admin.mfa_required";

    private readonly TestTokenIssuer _issuer = new();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------ claim parsing

    public static TheoryData<string> AcrCases() =>
    [
        "absent", "string 2", "string 1", "string 0", "string with leading space", "string with trailing space",
        "string 02", "string 2.0", "empty string", "upper-case word", "number 2", "boolean true",
        "two string claims 2 and 2", "two string claims 1 and 2", "one claim 2 and one claim 1",
    ];

    [Theory]
    [MemberData(nameof(AcrCases), DisableDiscoveryEnumeration = true)]
    public void HasMfaLevel_is_true_only_for_exactly_one_string_acr_claim_equal_to_2(string label)
    {
        var (claims, expected) = label switch
        {
            "absent" => (Array.Empty<Claim>(), false),
            "string 2" => (new[] { Acr("2") }, true),
            "string 1" => (new[] { Acr("1") }, false),
            "string 0" => (new[] { Acr("0") }, false),
            "string with leading space" => (new[] { Acr(" 2") }, false),
            "string with trailing space" => (new[] { Acr("2 ") }, false),
            "string 02" => (new[] { Acr("02") }, false),
            "string 2.0" => (new[] { Acr("2.0") }, false),
            "empty string" => (new[] { Acr(string.Empty) }, false),
            "upper-case word" => (new[] { Acr("TWO") }, false),
            "number 2" => (new[] { new Claim("acr", "2", ClaimValueTypes.Integer32) }, false),
            "boolean true" => (new[] { new Claim("acr", "true", ClaimValueTypes.Boolean) }, false),
            "two string claims 2 and 2" => (new[] { Acr("2"), Acr("2") }, false),
            "two string claims 1 and 2" => (new[] { Acr("1"), Acr("2") }, false),
            "one claim 2 and one claim 1" => (new[] { Acr("2"), new Claim("acr", "1", ClaimValueTypes.Integer32) }, false),
            _ => throw new ArgumentOutOfRangeException(nameof(label)),
        };

        var identity = new ClaimsIdentity([new Claim("sub", "dev-admin"), .. claims], "test");
        var caller = CallerIdentity.From(new ClaimsPrincipal(identity));

        caller.Should().NotBeNull();
        caller!.HasMfaLevel.Should().Be(expected, label);
    }

    [Fact]
    public void The_claim_type_is_matched_exactly_not_case_insensitively()
    {
        var identity = new ClaimsIdentity([new Claim("sub", "dev-admin"), new Claim("ACR", "2", ClaimValueTypes.String)], "test");

        CallerIdentity.From(new ClaimsPrincipal(identity))!.HasMfaLevel.Should().BeFalse();
    }

    private static Claim Acr(string value) => new("acr", value, ClaimValueTypes.String);

    // ------------------------------------------------------------------ handler

    [Theory]
    [InlineData(true, true, true, true, false)]
    [InlineData(true, true, false, false, true)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, true, true, false)]
    [InlineData(false, true, false, true, false)]
    [InlineData(false, false, false, true, false)]
    public async Task The_handler_succeeds_for_an_MFA_proven_admin_and_logs_one_warning_only_for_an_admin_without_the_proof(
        bool requireAdminMfa, bool isPlatformAdmin, bool hasMfaLevel, bool expectedSuccess, bool expectedWarning)
    {
        var provider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var caller = new RequestCaller();
        caller.Set(TenantResolution.NoTenant, "dev-admin", isPlatformAdmin, hasMfaLevel);
        var handler = new AdminMfaAuthorizationHandler(
            caller, new FixedOptionsMonitor(new AdminMfaOptions { RequireAdminMfa = requireAdminMfa }),
            loggerFactory.CreateLogger<AdminMfaAuthorizationHandler>());
        var requirement = new AdminMfaRequirement();
        var context = new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(new ClaimsIdentity("test")), resource: null);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().Be(expectedSuccess);
        provider.Records.Count(r => r.EventName == EventName).Should().Be(expectedWarning ? 1 : 0);
        provider.Records.Should().OnlyContain(r => r.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_tenant_scoped_caller_never_gets_the_proof_even_with_acr_2_and_logs_no_mfa_warning()
    {
        var provider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var caller = new RequestCaller();
        caller.Set(TenantResolution.For(TenantId.New()), "dev-dual", isPlatformAdmin: true, hasMfaLevel: true);
        var handler = new AdminMfaAuthorizationHandler(
            caller, new FixedOptionsMonitor(new AdminMfaOptions()), loggerFactory.CreateLogger<AdminMfaAuthorizationHandler>());
        var context = new AuthorizationHandlerContext([new AdminMfaRequirement()], new ClaimsPrincipal(new ClaimsIdentity("test")), null);

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse("RequestCaller.Set drops IsPlatformAdmin for a tenant caller");
        provider.Records.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ the real host

    [Theory]
    [InlineData("POST", "/api/admin/tenants/{0}/trial")]
    [InlineData("PUT", "/api/admin/tenants/{0}/overrides/pro.export")]
    [InlineData("DELETE", "/api/admin/tenants/{0}/overrides/pro.export")]
    public async Task An_admin_with_acr_2_is_accepted_and_one_without_it_is_refused_with_the_generic_403_and_one_warning(
        string method, string template)
    {
        var provider = new CapturingLoggerProvider();
        var stub = new CountingAdminCommands();
        await using var factory = CreateFactory(stub, configureLogging: logging => logging.AddProvider(provider));
        using var client = factory.CreateClient();
        var path = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, TestTokenIssuer.DevAliceTenantId);
        var subjectCanary = Canaries.Unique("admin-sub");

        using var accepted = await SendAsync(client, method, path, AdminToken(acr: "2"));
        accepted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        stub.Calls.Should().Be(1);

        foreach (var (label, token) in RefusedAdminTokens(subjectCanary))
        {
            provider.Records.Clear();
            using var refused = await SendAsync(client, method, path, token);

            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, label);
            refused.Headers.WwwAuthenticate.Should().BeEmpty(label);
            using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            body.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["type", "title", "status", "traceId"], label);

            var warnings = provider.Records.Where(r => r.EventName == EventName).ToList();
            warnings.Should().ContainSingle(label);
            warnings[0].Level.Should().Be(LogLevel.Warning);
            warnings[0].Contains(subjectCanary).Should().BeFalse(label);
            provider.Records.Where(r => r.Contains(subjectCanary)).Should().BeEmpty($"{label}: the sub never reaches a log message or state");
        }

        stub.Calls.Should().Be(1, "a refused admin never reaches the commands, let alone a database");
    }

    [Fact]
    public async Task The_refused_admin_warning_carries_the_hashed_user_id_through_the_enrichment_and_no_raw_sub()
    {
        var stub = new CountingAdminCommands();
        await using var factory = CreateFactory(
            stub,
            configureServices: services => services.AddSingleton<ILoggerProvider>(
                sp => new EnrichmentCapturingLoggerProvider(sp.GetRequiredService<Decisya.ServiceDefaults.Logging.ILogEnrichmentContext>())));
        using var client = factory.CreateClient();
        var capture = factory.Services.GetServices<ILoggerProvider>().OfType<EnrichmentCapturingLoggerProvider>().Single();
        var sub = Canaries.Unique("hashed-sub");

        using var refused = await SendAsync(
            client, "POST", $"/api/admin/tenants/{TestTokenIssuer.DevAliceTenantId}/trial", AdminToken(acr: "1", subject: sub));

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var record = capture.Records.Single(r => r.Category.EndsWith("AdminMfaAuthorizationHandler", StringComparison.Ordinal));
        record.UserIdHash.Should().NotBeNullOrEmpty().And.NotContain(sub);
        record.StateText.Should().NotContain(sub);
    }

    [Fact]
    public async Task A_tenant_user_with_acr_2_is_refused_by_the_role_and_logs_no_mfa_warning()
    {
        var provider = new CapturingLoggerProvider();
        await using var factory = CreateFactory(new CountingAdminCommands(), configureLogging: logging => logging.AddProvider(provider));
        using var client = factory.CreateClient();
        var claims = TestTokenIssuer.DefaultClaims();
        claims["roles"] = new[] { "tenant-user" };
        claims["acr"] = "2";

        using var response = await SendAsync(
            client, "POST", $"/api/admin/tenants/{TestTokenIssuer.DevAliceTenantId}/trial",
            TestTokenIssuer.IssueToken(claims, _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        provider.Records.Where(r => r.EventName == EventName).Should().BeEmpty();
    }

    [Fact]
    public async Task An_acr_that_is_a_json_array_of_one_string_2_is_accepted_because_it_is_one_string_claim()
    {
        var stub = new CountingAdminCommands();
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();
        var claims = TestTokenIssuer.DefaultClaims(subject: "dev-admin", tenantId: null);
        claims["roles"] = new[] { "platform-admin" };
        claims["acr"] = new[] { "2" };

        using var response = await SendAsync(
            client, "POST", $"/api/admin/tenants/{TestTokenIssuer.DevAliceTenantId}/trial",
            TestTokenIssuer.IssueToken(claims, _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task An_anonymous_call_is_still_the_bare_401_challenge()
    {
        await using var factory = CreateFactory(new CountingAdminCommands());
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            $"/api/admin/tenants/{TestTokenIssuer.DevAliceTenantId}/trial", content: null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Single().Scheme.Should().Be("Bearer");
    }

    /// <summary>Development with the switch off (the dev AppHost's run-mode setting) accepts the dev realm's acr "1" admin.</summary>
    [Fact]
    public async Task In_Development_the_switch_off_accepts_an_admin_without_the_proof_and_the_default_does_not()
    {
        var stub = new CountingAdminCommands();
        await using var off = ApiTestFactory.Create(
            _issuer,
            environmentName: "Development",
            extraConfiguration: [new("Authentication:RequireAdminMfa", "false")],
            configureServices: services => services.AddScoped<IEntitlementAdminCommands>(_ => stub));
        using var offClient = off.CreateClient();

        using var accepted = await SendAsync(
            offClient, "POST", $"/api/admin/tenants/{TestTokenIssuer.DevAliceTenantId}/trial", AdminToken(acr: "1"));
        accepted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var on = ApiTestFactory.Create(
            _issuer,
            environmentName: "Development",
            configureServices: services => services.AddScoped<IEntitlementAdminCommands>(_ => stub));
        using var onClient = on.CreateClient();

        using var refused = await SendAsync(
            onClient, "POST", $"/api/admin/tenants/{TestTokenIssuer.DevAliceTenantId}/trial", AdminToken(acr: "1"));
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, "RequireAdminMfa defaults to true, in Development too");
    }

    // ------------------------------------------------------------------ fail closed

    [Fact]
    public void The_setting_defaults_to_true_when_the_key_is_absent()
    {
        var options = Bind(configuration: []);

        options.RequireAdminMfa.Should().BeTrue();
        options.ValueIsNotABoolean.Should().BeFalse();
    }

    [Theory]
    [InlineData("Production", "false")]
    [InlineData("Production", "False")]
    [InlineData("Staging", "false")]
    [InlineData("", "false")]
    public void False_outside_Development_fails_validation_naming_the_key(string environment, string value)
    {
        var options = Bind([new("Authentication:RequireAdminMfa", value)]);

        var result = Validate(options, environment);

        result.Failed.Should().BeTrue();
        string.Join(' ', result.Failures!).Should().Contain("Authentication:RequireAdminMfa");
    }

    [Theory]
    [InlineData("Production", "true")]
    [InlineData("Development", "true")]
    [InlineData("Development", "false")]
    [InlineData("Staging", "true")]
    public void The_allowed_combinations_pass(string environment, string value)
    {
        Validate(Bind([new("Authentication:RequireAdminMfa", value)]), environment).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void A_value_that_is_not_a_boolean_fails_in_every_environment_and_never_echoes_the_value(string environment)
    {
        foreach (var junk in new[] { "yes", "1", "0", "off", "  ", string.Empty, "tru", "false;true", Canaries.Unique("not-a-bool") })
        {
            var options = Bind([new("Authentication:RequireAdminMfa", junk)]);

            options.RequireAdminMfa.Should().BeTrue("a value that does not parse keeps the fail-closed default");
            var result = Validate(options, environment);

            result.Failed.Should().BeTrue(junk);
            var message = string.Join(' ', result.Failures!);
            message.Should().Contain("Authentication:RequireAdminMfa");
            if (junk.Length > 3)
            {
                message.Should().NotContain(junk);
            }
        }
    }

    [Fact]
    public void AddApiAuthentication_wires_the_fail_closed_validator_into_ValidateOnStart_and_defaults_to_true()
    {
        using var absent = BuildHost(Environments.Production, []);
        absent.Services.GetRequiredService<IOptions<AdminMfaOptions>>().Value.RequireAdminMfa.Should().BeTrue();
        Record.Exception(absent.Services.GetRequiredService<IStartupValidator>().Validate).Should().BeNull();

        using var off = BuildHost(Environments.Production, [new("Authentication:RequireAdminMfa", "false")]);
        var exception = Record.Exception(off.Services.GetRequiredService<IStartupValidator>().Validate);
        exception.Should().BeOfType<OptionsValidationException>();
        exception!.Message.Should().Contain("Authentication:RequireAdminMfa");

        var canary = Canaries.Unique("junk");
        using var junk = BuildHost(Environments.Development, [new("Authentication:RequireAdminMfa", canary)]);
        var junkException = Record.Exception(junk.Services.GetRequiredService<IStartupValidator>().Validate);
        junkException.Should().BeOfType<OptionsValidationException>();
        junkException!.Message.Should().NotContain(canary);

        using var development = BuildHost(Environments.Development, [new("Authentication:RequireAdminMfa", "false")]);
        Record.Exception(development.Services.GetRequiredService<IStartupValidator>().Validate).Should().BeNull();
    }

    [Fact]
    public async Task The_policy_is_registered_with_the_authentication_and_sub_requirements_and_the_mfa_requirement()
    {
        using var host = BuildHost(Environments.Production, []);

        var policy = await host.Services.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(AdminMfaPolicy.Name);

        policy.Should().NotBeNull();
        policy!.Requirements.OfType<AdminMfaRequirement>().Should().ContainSingle();
        policy.Requirements.Should().Contain(r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
        AdminMfaPolicy.Name.Should().Be("Api.AdminMfa");
    }

    // ------------------------------------------------------------------ helpers

    private static Microsoft.AspNetCore.Builder.WebApplication BuildHost(string environment, KeyValuePair<string, string?>[] configuration)
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(
            new Microsoft.AspNetCore.Builder.WebApplicationOptions { EnvironmentName = environment });
        builder.Configuration.AddInMemoryCollection(
        [
            new KeyValuePair<string, string?>("Api:Jwt:Authority", "https://idp.example/realms/decisya"),
            .. configuration,
        ]);
        builder.AddApiAuthentication();
        return builder.Build();
    }

    private static AdminMfaOptions Bind(KeyValuePair<string, string?>[] configuration)
    {
        var root = new ConfigurationBuilder().AddInMemoryCollection(configuration).Build();
        var options = new AdminMfaOptions();
        new AdminMfaOptionsSetup(root).Configure(options);
        return options;
    }

    private static ValidateOptionsResult Validate(AdminMfaOptions options, string environment) =>
        new AdminMfaOptionsEnvironmentValidator(new TestHostEnvironment(environment)).Validate(Options.DefaultName, options);

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> CreateFactory(
        CountingAdminCommands stub,
        Action<IServiceCollection>? configureServices = null,
        Action<ILoggingBuilder>? configureLogging = null) =>
        ApiTestFactory.Create(
            _issuer,
            environmentName: "Production",
            configureServices: services =>
            {
                services.AddScoped<IEntitlementAdminCommands>(_ => stub);
                configureServices?.Invoke(services);
            },
            configureLogging: configureLogging);

    private string AdminToken(string? acr, string subject = "dev-admin")
    {
        var claims = TestTokenIssuer.DefaultClaims(subject: subject, tenantId: null);
        claims["roles"] = new[] { "platform-admin" };
        if (acr is not null)
        {
            claims["acr"] = acr;
        }

        return TestTokenIssuer.IssueToken(claims, _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256);
    }

    private IEnumerable<(string Label, string Token)> RefusedAdminTokens(string subject)
    {
        yield return ("no acr", AdminToken(acr: null, subject));
        yield return ("acr 1", AdminToken("1", subject));
        yield return ("acr 0", AdminToken("0", subject));
        yield return ("acr ' 2'", AdminToken(" 2", subject));
        yield return ("acr '2 '", AdminToken("2 ", subject));
        yield return ("acr '02'", AdminToken("02", subject));

        var number = TestTokenIssuer.DefaultClaims(subject: subject, tenantId: null);
        number["roles"] = new[] { "platform-admin" };
        number["acr"] = 2; // a JSON number, not the string "2"
        yield return ("acr number 2", TestTokenIssuer.IssueToken(number, _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256));

        var many = TestTokenIssuer.DefaultClaims(subject: subject, tenantId: null);
        many["roles"] = new[] { "platform-admin" };
        many["acr"] = new[] { "1", "2" };
        yield return ("acr array 1,2", TestTokenIssuer.IssueToken(many, _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256));
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path, string token)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (method != "DELETE")
        {
            request.Content = new StringContent("""{"reason":"ok"}""", Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed class CountingAdminCommands : IEntitlementAdminCommands
    {
        private int _calls;

        public int Calls => _calls;

        public Task<EntitlementAdminResult> StartTrialAsync(TenantId tenantId, CancellationToken cancellationToken = default) => Record();

        public Task<EntitlementAdminResult> GrantOverrideAsync(
            TenantId tenantId, FeatureKey feature, string reason, Instant? expiresAt, CancellationToken cancellationToken = default) => Record();

        public Task<EntitlementAdminResult> RevokeOverrideAsync(
            TenantId tenantId, FeatureKey feature, CancellationToken cancellationToken = default) => Record();

        private Task<EntitlementAdminResult> Record()
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(EntitlementAdminResult.Succeeded);
        }
    }

    private sealed class FixedOptionsMonitor(AdminMfaOptions value) : IOptionsMonitor<AdminMfaOptions>
    {
        public AdminMfaOptions CurrentValue => value;

        public AdminMfaOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<AdminMfaOptions, string?> listener) => null;
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Decisya.Api.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
