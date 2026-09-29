using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Decisya.Modules.Tenancy.Domain;
using Decisya.Modules.Tenancy.Infrastructure;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// Issue #21, G5: the HTTP-level Gherkin scenarios of Stories 1, 2 and 3 over a real
/// <c>Decisya.Api</c> host and a real, migrated Postgres database. Each test gets its own
/// fresh database (<see cref="TenancyDatabaseFixture.CreateFreshDatabaseAsync"/>) so a row count
/// is exact even while other tests run in parallel. Connected as the database owner (see
/// <see cref="TenancyDatabaseFixture"/>'s remarks, #83).
/// </summary>
[Trait("Category", "Integration")]
public sealed class TenancyEndpointScenarioTests(TenancyDatabaseFixture database) : IDisposable
{
    private readonly TestTokenIssuer _issuer = new();

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Story 1 scenario 1.</summary>
    [Fact]
    public async Task A_tenant_users_very_first_authenticated_request_creates_her_tenant_and_her_Owner_membership()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await database.CreateFreshDatabaseAsync(ct);
        var tenant = Guid.NewGuid().ToString();
        var alice = Canaries.Unique("alice");

        await using var factory = ApiTestFactory.Create(_issuer, extraConfiguration: [new("ConnectionStrings:tenancy", connectionString)]);
        using var client = factory.CreateClient();

        (await CountAsync(connectionString, "tenants")).Should().Be(0, "the tenant must not exist before the first request");

        using var response = await GetAsync(client, "/api/tenancy/me", _issuer.IssueValidAccessToken(subject: alice, tenantId: tenant), ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var verify = CreateContext(connectionString, tenant);
        (await verify.Tenants.CountAsync(ct)).Should().Be(1);
        var membership = await verify.Memberships.SingleAsync(ct);
        membership.UserId.Should().Be(alice);
        membership.Role.Should().Be(TenantRole.Owner);
    }

    /// <summary>Story 1 scenario 2.</summary>
    [Fact]
    public async Task A_later_request_from_the_same_tenant_user_reuses_the_existing_tenant_and_membership_never_duplicating_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await database.CreateFreshDatabaseAsync(ct);
        var tenant = Guid.NewGuid().ToString();
        var alice = Canaries.Unique("alice");

        await using var factory = ApiTestFactory.Create(_issuer, extraConfiguration: [new("ConnectionStrings:tenancy", connectionString)]);
        using var client = factory.CreateClient();

        using (var first = await GetAsync(client, "/api/tenancy/me", _issuer.IssueValidAccessToken(subject: alice, tenantId: tenant), ct))
        {
            first.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var second = await GetAsync(client, "/api/tenancy/me", _issuer.IssueValidAccessToken(subject: alice, tenantId: tenant), ct);

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountAsync(connectionString, "tenants")).Should().Be(1);
        (await CountAsync(connectionString, "memberships")).Should().Be(1);
    }

    /// <summary>Story 1 scenario 3 and Story 2 scenario 2.</summary>
    [Fact]
    public async Task A_platform_admins_request_never_creates_a_tenant_and_sees_no_tenant_data()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await database.CreateFreshDatabaseAsync(ct);

        await using var factory = ApiTestFactory.Create(_issuer, extraConfiguration: [new("ConnectionStrings:tenancy", connectionString)]);
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, "/api/tenancy/me", _issuer.IssueValidAccessToken(subject: Canaries.Unique("admin"), tenantId: null), ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        body.RootElement.TryGetProperty("tenant", out _).Should().BeFalse();
        body.RootElement.TryGetProperty("membership", out _).Should().BeFalse();
        (await CountAsync(connectionString, "tenants")).Should().Be(0);
        (await CountAsync(connectionString, "memberships")).Should().Be(0);
    }

    /// <summary>Story 2 scenario 1.</summary>
    [Fact]
    public async Task A_tenant_user_sees_her_tenant_and_her_own_membership()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await database.CreateFreshDatabaseAsync(ct);
        var tenant = Guid.NewGuid().ToString();

        await using var factory = ApiTestFactory.Create(_issuer, extraConfiguration: [new("ConnectionStrings:tenancy", connectionString)]);
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, "/api/tenancy/me", _issuer.IssueValidAccessToken(subject: Canaries.Unique("alice"), tenantId: tenant), ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        body.RootElement.GetProperty("tenant").GetProperty("id").GetString().Should().Be(tenant);
        body.RootElement.GetProperty("membership").GetProperty("role").GetString().Should().Be("Owner");
    }

    /// <summary>Story 2 scenario 3.</summary>
    [Fact]
    public async Task A_tenant_user_never_sees_another_tenants_data_through_this_endpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await database.CreateFreshDatabaseAsync(ct);
        var tenantA = Guid.NewGuid().ToString();
        var tenantB = Guid.NewGuid().ToString();

        await using var factory = ApiTestFactory.Create(_issuer, extraConfiguration: [new("ConnectionStrings:tenancy", connectionString)]);
        using var client = factory.CreateClient();

        using (await GetAsync(client, "/api/tenancy/me", _issuer.IssueValidAccessToken(subject: Canaries.Unique("bob"), tenantId: tenantB), ct))
        {
        }

        using var response = await GetAsync(client, "/api/tenancy/me", _issuer.IssueValidAccessToken(subject: Canaries.Unique("alice"), tenantId: tenantA), ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync(ct);
        using var body = JsonDocument.Parse(text);
        body.RootElement.GetProperty("tenant").GetProperty("id").GetString().Should().Be(tenantA);
        text.Should().NotContain(tenantB);
    }

    /// <summary>Story 3 scenario 1 and scenario 4 (the Owner lists only her own tenant's members).</summary>
    [Fact]
    public async Task The_Owner_lists_her_tenants_members_and_never_sees_another_tenants_members()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await database.CreateFreshDatabaseAsync(ct);
        var tenantA = Guid.NewGuid().ToString();
        var tenantB = Guid.NewGuid().ToString();
        var alice = Canaries.Unique("alice");
        var bob = Canaries.Unique("bob");

        await using var factory = ApiTestFactory.Create(_issuer, extraConfiguration: [new("ConnectionStrings:tenancy", connectionString)]);
        using var client = factory.CreateClient();

        using (await GetAsync(client, "/api/tenancy/me", _issuer.IssueValidAccessToken(subject: bob, tenantId: tenantB), ct))
        {
        }

        using var response = await GetAsync(client, "/api/tenancy/members", _issuer.IssueValidAccessToken(subject: alice, tenantId: tenantA), ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync(ct);
        using var body = JsonDocument.Parse(text);
        var members = body.RootElement.GetProperty("members").EnumerateArray().ToList();
        members.Should().ContainSingle();
        members[0].GetProperty("userId").GetString().Should().Be(alice);
        members[0].GetProperty("role").GetString().Should().Be("Owner");
        text.Should().NotContain(bob);
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, ct);
    }

    private static TenancyDbContext CreateContext(string connectionString, string tenantIdText)
    {
        var optionsBuilder = new DbContextOptionsBuilder<TenancyDbContext>();
        TenancyDbContextOptions.Configure(optionsBuilder, connectionString);
        return new TenancyDbContext(
            optionsBuilder.Options, new TestCurrentTenant { Resolution = TenantResolution.For(TenantId.Parse(tenantIdText)) });
    }

    /// <summary>Counts every row in the module's table, across all tenants (a raw count: the fresh database holds only this test's rows).</summary>
    private static async Task<long> CountAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var sql = table switch
        {
            "tenants" => "select count(*) from tenancy.tenants",
            "memberships" => "select count(*) from tenancy.memberships",
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
