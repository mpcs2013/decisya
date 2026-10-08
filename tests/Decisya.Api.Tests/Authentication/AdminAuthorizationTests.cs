using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Entitlements.Contracts.Admin;
using Decisya.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NodaTime;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// G3 G4-25-01 (the policy half), G4-25-02 and G4-25-03 against the real host, with no Docker.
/// The tenancy and entitlements connection strings are placeholders (<c>Host=db.invalid</c>), so a
/// 403 here is itself proof that no database command ran for the caller (NFR-38): a command would
/// have thrown and turned the 403 into a 500. <see cref="IEntitlementAdminCommands"/> is replaced
/// by a recording stub, so a success never needs a database either.
/// </summary>
public class AdminAuthorizationTests : IDisposable
{
    private const string AdminRole = "platform-admin";
    private const string ExistingTenant = TestTokenIssuer.DevAliceTenantId;
    private const string UnknownTenant = "2f1a8d5e-3b4c-4e6f-9a7b-1c2d3e4f5a6b";

    private readonly TestTokenIssuer _issuer = new();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    public static TheoryData<string, string> Verbs => new()
    {
        { "POST", "/api/admin/tenants/{0}/trial" },
        { "PUT", "/api/admin/tenants/{0}/overrides/{1}" },
        { "DELETE", "/api/admin/tenants/{0}/overrides/{1}" },
    };

    /// <summary>Every non-admin shape of caller (G1 Story 1 caller table).</summary>
    public static TheoryData<string> NonAdminCallers =>
    [
        "tenant-user-with-tenant",
        "admin-role-with-tenant-id",
        "role-only-in-realm-access",
        "role-in-groups",
        "wrong-case-role",
        "role-with-suffix",
        "no-roles-no-tenant",
        "non-string-roles-next-to-admin",
    ];

    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task A_platform_admin_with_no_tenant_passes_the_policy_and_gets_204(string method, string template)
    {
        var stub = new RecordingAdminCommands();
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, method, Url(template, ExistingTenant, "pro.export"), Token("admin"), jsonBody: """{"reason":"ok"}""");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        stub.Calls.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task A_role_array_with_tenant_user_and_platform_admin_and_no_tenant_is_an_admin(string method, string template)
    {
        var stub = new RecordingAdminCommands();
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();
        var claims = TestTokenIssuer.DefaultClaims(subject: "dev-admin", tenantId: null);
        claims["roles"] = new[] { "tenant-user", AdminRole };
        claims["acr"] = "2"; // #121

        using var response = await SendAsync(client, method, Url(template, ExistingTenant, "pro.export"), Issue(claims), jsonBody: """{"reason":"ok"}""");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        stub.Calls.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(NonAdminCallers), DisableDiscoveryEnumeration = true)]
    public async Task Every_non_admin_gets_the_generic_403_on_every_verb_and_never_reaches_the_commands(string caller)
    {
        var stub = new RecordingAdminCommands();
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        foreach (var (method, template) in new[]
        {
            ("POST", "/api/admin/tenants/{0}/trial"),
            ("PUT", "/api/admin/tenants/{0}/overrides/{1}"),
            ("DELETE", "/api/admin/tenants/{0}/overrides/{1}"),
        })
        {
            using var response = await SendAsync(client, method, Url(template, ExistingTenant, "pro.export"), Token(caller), jsonBody: """{"reason":"ok"}""");

            await AssertGenericForbiddenAsync(response, $"{caller} {method}");
        }

        stub.Calls.Should().Be(0, "a refused caller never reaches the facade, let alone a database");
    }

    /// <summary>G4-25-03: one identical 403, whatever the input, so nothing about the tenant, the key or the body is learnable.</summary>
    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task A_non_admin_gets_the_identical_403_for_every_kind_of_input(string method, string template)
    {
        var stub = new RecordingAdminCommands();
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();
        var token = Token("tenant-user-with-tenant");

        var inputs = new (string Tenant, string Feature, string? Body, string? ContentType)[]
        {
            (ExistingTenant, "pro.export", """{"reason":"ok"}""", "application/json"),
            (UnknownTenant, "pro.export", """{"reason":"ok"}""", "application/json"),
            ("not-a-guid", "pro.export", """{"reason":"ok"}""", "application/json"),
            ("00000000-0000-0000-0000-000000000000", "pro.export", """{"reason":"ok"}""", "application/json"),
            (ExistingTenant, "nosuch.feature", """{"reason":"ok"}""", "application/json"),
            (ExistingTenant, "Malformed", """{"reason":"ok"}""", "application/json"),
            (ExistingTenant, "pro.export", "{not json", "application/json"),
            (ExistingTenant, "pro.export", """{"reason":42}""", "application/json"),
            (ExistingTenant, "pro.export", "plain text", "text/plain"),
            (ExistingTenant, "pro.export", null, null),
        };

        var shapes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (tenant, feature, body, contentType) in inputs)
        {
            using var response = await SendAsync(
                client, method, Url(template, tenant, feature), token, jsonBody: body, contentType: contentType);

            shapes.Add(await AssertGenericForbiddenAsync(response, $"{method} {tenant} {feature} {body}"));
        }

        shapes.Should().ContainSingle("every non-admin input must get the identical 403 body");
        stub.Calls.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task A_forged_admin_header_query_or_cookie_never_grants_the_role(string method, string template)
    {
        var stub = new RecordingAdminCommands();
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(
            new HttpMethod(method), Url(template, ExistingTenant, "pro.export") + "?roles=platform-admin&role=platform-admin&isPlatformAdmin=true");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("tenant-user-with-tenant"));
        request.Headers.TryAddWithoutValidation("X-Platform-Admin", "true");
        request.Headers.TryAddWithoutValidation("X-Roles", AdminRole);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Roles", AdminRole);
        request.Headers.TryAddWithoutValidation("X-Tenant-Id", ExistingTenant);
        request.Headers.TryAddWithoutValidation("Cookie", $"roles={AdminRole}");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        await AssertGenericForbiddenAsync(response, method);
        stub.Calls.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task An_anonymous_call_is_the_bare_401_challenge(string method, string template)
    {
        await using var factory = CreateFactory(new RecordingAdminCommands());
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), Url(template, ExistingTenant, "pro.export"));
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Single().Scheme.Should().Be("Bearer");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_anonymous_call_to_an_unknown_admin_path_is_a_401()
    {
        await using var factory = CreateFactory(new RecordingAdminCommands());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/admin/x", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>The handler's own Forbidden (stub) is the same 403 body as the policy's.</summary>
    [Fact]
    public async Task A_facade_Forbidden_and_the_policy_403_have_the_identical_body_and_no_code()
    {
        var stub = new RecordingAdminCommands
        {
            Result = EntitlementAdminResult.Failed(EntitlementAdminStatus.Forbidden, EntitlementAdminErrorCodes.Forbidden),
        };
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();
        var path = Url("/api/admin/tenants/{0}/trial", ExistingTenant, "pro.export");

        using var fromFacade = await SendAsync(client, "POST", path, Token("admin"));
        using var fromPolicy = await SendAsync(client, "POST", path, Token("tenant-user-with-tenant"));

        var facadeShape = await AssertGenericForbiddenAsync(fromFacade, "facade");
        var policyShape = await AssertGenericForbiddenAsync(fromPolicy, "policy");
        facadeShape.Should().Be(policyShape);
        stub.Calls.Should().Be(1);
    }

    /// <summary>G4-25-03: every 403 source has exactly type, title, status and traceId, and the same values.</summary>
    [Fact]
    public async Task Every_403_source_has_the_identical_shape()
    {
        await using var factory = CreateFactory(new RecordingAdminCommands(), configureServices: services =>
            services.AddAuthorizationBuilder().AddPolicy("Test.Never", policy => policy.RequireAssertion(_ => false)),
            testEndpointsCanary: Canaries.Unique("shape"));
        using var client = factory.CreateClient();

        // 1. pre-routing 403 (#21): a token with no sub claim.
        var noSub = Issue(TestTokenIssuer.DefaultClaims(subject: null));
        using var preRouting = await SendAsync(client, "GET", "/api/whoami", noSub);
        // 2. Tenancy.Owner policy 403: a caller with no tenant, which needs no database.
        using var ownerPolicy = await SendAsync(client, "GET", "/api/tenancy/members", Token("no-roles-no-tenant"));
        // 3. the admin policy 403.
        using var adminPolicy = await SendAsync(client, "POST", Url("/api/admin/tenants/{0}/trial", ExistingTenant, "x"), Token("tenant-user-with-tenant"));

        var shapes = new[]
        {
            await AssertGenericForbiddenAsync(preRouting, "pre-routing"),
            await AssertGenericForbiddenAsync(ownerPolicy, "Tenancy.Owner"),
            await AssertGenericForbiddenAsync(adminPolicy, "admin policy"),
        };

        shapes.Distinct().Should().ContainSingle();
    }

    /// <summary>R-2 / Q2: the dual claim logs one fixed-text Warning and no claim value.</summary>
    [Fact]
    public async Task An_admin_role_with_a_tenant_id_logs_a_Warning_without_any_claim_value()
    {
        var provider = new CapturingLoggerProvider();
        await using var factory = CreateFactory(new RecordingAdminCommands(), configureLogging: logging => logging.AddProvider(provider));
        using var client = factory.CreateClient();
        var tenant = Guid.NewGuid().ToString();
        var claims = TestTokenIssuer.DefaultClaims(subject: Canaries.Unique("dual-sub"), tenantId: tenant);
        claims["roles"] = new[] { AdminRole };

        using var response = await SendAsync(client, "POST", Url("/api/admin/tenants/{0}/trial", tenant, "x"), Issue(claims));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var warnings = provider.Records
            .Where(r => r.Level == LogLevel.Warning && r.Category.EndsWith("CallerContextMiddleware", StringComparison.Ordinal))
            .ToList();
        warnings.Should().ContainSingle();
        warnings[0].Contains(tenant).Should().BeFalse();
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> CreateFactory(
        RecordingAdminCommands stub,
        Action<IServiceCollection>? configureServices = null,
        string? testEndpointsCanary = null,
        Action<ILoggingBuilder>? configureLogging = null) =>
        ApiTestFactory.Create(
            _issuer,
            environmentName: "Production",
            configureServices: services =>
            {
                services.AddScoped<IEntitlementAdminCommands>(_ => stub);
                configureServices?.Invoke(services);
            },
            testEndpointsCanary: testEndpointsCanary,
            configureLogging: configureLogging);

    private string Token(string caller)
    {
        var claims = caller switch
        {
            "admin" => TestTokenIssuer.DefaultClaims(subject: "dev-admin", tenantId: null),
            "tenant-user-with-tenant" => TestTokenIssuer.DefaultClaims(),
            "admin-role-with-tenant-id" => TestTokenIssuer.DefaultClaims(subject: "dev-dual"),
            "no-roles-no-tenant" => TestTokenIssuer.DefaultClaims(subject: "dev-nobody", tenantId: null),
            _ => TestTokenIssuer.DefaultClaims(subject: "dev-odd", tenantId: null),
        };

        switch (caller)
        {
            case "admin":
            case "admin-role-with-tenant-id":
                claims["roles"] = new[] { AdminRole };
                claims["acr"] = "2"; // #121: the admin is MFA-proven; the dual-claim caller is refused for its tenant_id, not for MFA.
                break;
            case "tenant-user-with-tenant":
                claims["roles"] = new[] { "tenant-user" };
                break;
            case "role-only-in-realm-access":
                claims["realm_access"] = new Dictionary<string, object> { ["roles"] = new[] { AdminRole } };
                claims["roles"] = new[] { "tenant-user" };
                break;
            case "role-in-groups":
                claims["groups"] = new[] { AdminRole };
                break;
            case "wrong-case-role":
                claims["roles"] = new[] { "Platform-Admin" };
                break;
            case "role-with-suffix":
                claims["roles"] = new[] { "platform-admin-x" };
                break;
            case "non-string-roles-next-to-admin":
                claims["roles"] = new object[] { AdminRole, new Dictionary<string, object> { ["x"] = 1 } };
                break;
        }

        return Issue(claims);
    }

    private string Issue(Dictionary<string, object> claims) =>
        TestTokenIssuer.IssueToken(claims, _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256);

    private static string Url(string template, string tenant, string feature) => string.Format(
        System.Globalization.CultureInfo.InvariantCulture, template, tenant, feature);

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, string method, string path, string token, string? jsonBody = null, string? contentType = "application/json")
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, contentType ?? "application/json");
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Asserts the generic 403 and returns its normalised shape (the body without <c>traceId</c>'s value).</summary>
    private static async Task<string> AssertGenericForbiddenAsync(HttpResponseMessage response, string label)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, label);
        response.Headers.WwwAuthenticate.Should().BeEmpty(label);
        response.Headers.CacheControl!.NoStore.Should().BeTrue(label);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        document.RootElement.EnumerateObject().Select(p => p.Name).Should()
            .BeEquivalentTo(["type", "title", "status", "traceId"], label);
        document.RootElement.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty(label);

        return $"{document.RootElement.GetProperty("type")}|{document.RootElement.GetProperty("title")}|{document.RootElement.GetProperty("status")}";
    }

    private sealed class RecordingAdminCommands : IEntitlementAdminCommands
    {
        private int _calls;

        public int Calls => _calls;

        public EntitlementAdminResult Result { get; init; } = EntitlementAdminResult.Succeeded;

        public Task<EntitlementAdminResult> StartTrialAsync(TenantId tenantId, CancellationToken cancellationToken = default) => Record();

        public Task<EntitlementAdminResult> GrantOverrideAsync(
            TenantId tenantId, FeatureKey feature, string reason, Instant? expiresAt, CancellationToken cancellationToken = default) => Record();

        public Task<EntitlementAdminResult> RevokeOverrideAsync(
            TenantId tenantId, FeatureKey feature, CancellationToken cancellationToken = default) => Record();

        private Task<EntitlementAdminResult> Record()
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(Result);
        }
    }
}
