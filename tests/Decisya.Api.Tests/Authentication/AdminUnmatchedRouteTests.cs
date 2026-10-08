using System.Net;
using System.Net.Http.Headers;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// G3 S-3 (T-06), pinning today's behaviour: an authenticated caller who is not an admin gets 404 on an unknown
/// <c>/api/admin/x</c> path and 405 on a wrong verb for an admin route, not the policy 403 (the policy applies
/// only once a route matches). It reveals only the public route shape. For a tenant caller the 405 runs after
/// the #21 membership gate (a query and possibly a JIT write), which is existing behaviour on every route.
/// NFR-38 is therefore read as "the three admin routes". A group catch-all returning 404 behind the policy is
/// the later fix, if wanted (#83).
/// </summary>
[Trait("Category", "Unit")]
public sealed class AdminUnmatchedRouteTests : IDisposable
{
    private const string Tenant = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    private readonly TestTokenIssuer _issuer = new();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    public static TheoryData<string, string> UnknownPaths => new()
    {
        { "GET", "/api/admin/x" },
        { "POST", "/api/admin/x" },
        { "GET", "/api/admin" },
        { "PUT", $"/api/admin/tenants/{Tenant}" },
        { "POST", $"/api/admin/tenants/{Tenant}/overrides" },
    };

    public static TheoryData<string, string> WrongVerbs => new()
    {
        { "GET", $"/api/admin/tenants/{Tenant}/trial" },
        { "PUT", $"/api/admin/tenants/{Tenant}/trial" },
        { "GET", $"/api/admin/tenants/{Tenant}/overrides/forecasting.scenarios" },
        { "POST", $"/api/admin/tenants/{Tenant}/overrides/forecasting.scenarios" },
    };

    private string TenantLessNonAdminToken() =>
        TestTokenIssuer.IssueToken(
            TestTokenIssuer.DefaultClaims(subject: "dev-nobody", tenantId: null), _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256);

    private string AdminToken()
    {
        var claims = TestTokenIssuer.DefaultClaims(subject: "dev-admin", tenantId: null);
        claims["roles"] = new[] { "platform-admin" };
        claims["acr"] = "2"; // #121: the MFA proof Keycloak's step-up flow gives an admin after the OTP.
        return TestTokenIssuer.IssueToken(claims, _issuer.RsaSigningKey, SecurityAlgorithms.RsaSha256);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path, string token)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Theory]
    [MemberData(nameof(UnknownPaths))]
    public async Task An_authenticated_caller_with_no_tenant_gets_404_on_an_unknown_admin_path_whether_admin_or_not(string method, string path)
    {
        await using var factory = ApiTestFactory.Create(_issuer, environmentName: "Production");
        using var client = factory.CreateClient();

        foreach (var token in new[] { TenantLessNonAdminToken(), AdminToken() })
        {
            using var response = await SendAsync(client, method, path, token);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{method} {path}");
        }
    }

    [Theory]
    [MemberData(nameof(WrongVerbs))]
    public async Task An_authenticated_caller_with_no_tenant_gets_405_on_a_wrong_verb_for_an_admin_route_whether_admin_or_not(string method, string path)
    {
        await using var factory = ApiTestFactory.Create(_issuer, environmentName: "Production");
        using var client = factory.CreateClient();

        foreach (var token in new[] { TenantLessNonAdminToken(), AdminToken() })
        {
            using var response = await SendAsync(client, method, path, token);

            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed, $"{method} {path}");
        }
    }

    [Fact]
    public async Task An_anonymous_caller_still_gets_401_on_an_unknown_path_and_a_wrong_verb()
    {
        await using var factory = ApiTestFactory.Create(_issuer, environmentName: "Production");
        using var client = factory.CreateClient();

        using var unknown = await client.GetAsync("/api/admin/x", TestContext.Current.CancellationToken);
        using var wrongVerb = await client.GetAsync($"/api/admin/tenants/{Tenant}/trial", TestContext.Current.CancellationToken);

        unknown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        wrongVerb.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

/// <summary>The tenant-caller half of S-3 (needs the #21 membership gate, so a real database).</summary>
[Trait("Category", "Integration")]
public sealed class AdminUnmatchedRouteTenantCallerTests : IDisposable
{
    private const string Tenant = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

    private readonly TenancyDatabaseFixture _database;
    private readonly TestTokenIssuer _issuer = new();

    public AdminUnmatchedRouteTenantCallerTests(TenancyDatabaseFixture database)
    {
        _database = database;
    }

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    public static TheoryData<string, string> UnknownPaths => AdminUnmatchedRouteTests.UnknownPaths;

    public static TheoryData<string, string> WrongVerbs => AdminUnmatchedRouteTests.WrongVerbs;

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path, string token)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Theory]
    [MemberData(nameof(UnknownPaths))]
    public async Task A_tenant_caller_gets_404_on_an_unknown_admin_path(string method, string path)
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await _database.EnsureStartedAsync(ct);
        await using var factory = ApiTestFactory.Create(
            _issuer, environmentName: "Production", extraConfiguration: [new("ConnectionStrings:tenancy", connectionString)]);
        using var client = factory.CreateClient();
        var token = _issuer.IssueValidAccessToken(subject: Canaries.Unique("s3-user"), tenantId: Guid.NewGuid().ToString());

        using var response = await SendAsync(client, method, path, token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{method} {path}");
    }

    [Theory]
    [MemberData(nameof(WrongVerbs))]
    public async Task A_tenant_caller_gets_405_on_a_wrong_verb_for_an_admin_route_after_the_membership_gate(string method, string path)
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await _database.EnsureStartedAsync(ct);
        await using var factory = ApiTestFactory.Create(
            _issuer, environmentName: "Production", extraConfiguration: [new("ConnectionStrings:tenancy", connectionString)]);
        using var client = factory.CreateClient();
        var tenant = Guid.NewGuid();
        var user = Canaries.Unique("s3-user");
        var token = _issuer.IssueValidAccessToken(subject: user, tenantId: tenant.ToString());

        using var response = await SendAsync(client, method, path, token);

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed, $"{method} {path}");
        // Pinned, existing #21 behaviour on every route: the gate ran before the 405 and JIT-provisioned the caller's own tenant.
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM tenancy.memberships WHERE tenant_id = @t", connection);
        command.Parameters.AddWithValue("t", tenant);
        Convert.ToInt64(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture).Should().Be(1);
    }
}
