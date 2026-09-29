using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Decisya.Modules.Tenancy.Domain;
using Decisya.Modules.Tenancy.Infrastructure;
using Decisya.ServiceDefaults.Logging;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Decisya.Api.Tests.Authentication;

/// <summary>
/// Issue #21, G3 G4-21-01 and G4-21-02: proves tenant isolation and the membership gate against
/// a real, migrated Postgres database (<see cref="TenancyDatabaseFixture"/>), connected as the
/// database owner, not as the least-privilege <c>decisya_tenancy</c> role (recorded in #83; that
/// role's restriction is covered by <c>Decisya.Infrastructure.Migrator.Tests</c>) — not a schema
/// built by <c>EnsureCreated</c>. Every test generates its own random tenant id and user
/// ids, never a fixed constant: the fixture's database is shared by every test in this
/// assembly.
/// </summary>
[Trait("Category", "Integration")]
public class TenancyPipelineIntegrationTests : IDisposable
{
    private readonly TenancyDatabaseFixture _database;
    private readonly TestTokenIssuer _issuer = new();

    public TenancyPipelineIntegrationTests(TenancyDatabaseFixture database)
    {
        _database = database;
    }

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// G4-21-01's own red test: 2 × 25 interleaved parallel requests from two different tenant
    /// users. Every response carries only its own caller's tenant, and every captured log
    /// record's own (tenant_id, hashed user_id) pairing is one of the two expected pairs —
    /// never the other tenant's id paired with this caller's hash, and never the raw sub.
    /// </summary>
    [Fact]
    public async Task Interleaved_requests_from_two_tenants_never_cross_and_every_log_pairing_stays_consistent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tenancyConnectionString = await _database.EnsureStartedAsync(cancellationToken);

        var tenantA = Guid.NewGuid().ToString();
        var tenantB = Guid.NewGuid().ToString();
        var userA = Canaries.Unique("tenant-a-user");
        var userB = Canaries.Unique("tenant-b-user");

        await using var factory = ApiTestFactory.Create(
            _issuer,
            extraConfiguration: [new("ConnectionStrings:tenancy", tenancyConnectionString)],
            configureServices: services => services.AddSingleton<ILoggerProvider>(
                sp => new EnrichmentCapturingLoggerProvider(sp.GetRequiredService<ILogEnrichmentContext>())));

        using var client = factory.CreateClient();
        var capturingProvider = factory.Services.GetServices<ILoggerProvider>()
            .OfType<EnrichmentCapturingLoggerProvider>()
            .Single();

        var tokenA = _issuer.IssueValidAccessToken(subject: userA, tenantId: tenantA);
        var tokenB = _issuer.IssueValidAccessToken(subject: userB, tenantId: tenantB);

        var requests = new List<Task<(string ExpectedTenant, HttpResponseMessage Response)>>();
        for (var i = 0; i < 25; i++)
        {
            requests.Add(SendMeAsync(client, tokenA, tenantA, cancellationToken));
            requests.Add(SendMeAsync(client, tokenB, tenantB, cancellationToken));
        }

        var results = await Task.WhenAll(requests);

        foreach (var (expectedTenant, response) in results)
        {
            using (response)
            {
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                using var body = JsonDocument.Parse(text);
                body.RootElement.GetProperty("tenant").GetProperty("id").GetString().Should().Be(expectedTenant);
            }
        }

        var recordsWithBothFields = capturingProvider.Records
            .Where(record => record.TenantId is not null && record.UserIdHash is not null)
            .ToList();

        recordsWithBothFields.Should().NotBeEmpty(
            "interleaved requests against a real tenant should have produced at least some enriched log records");

        var hashesByTenant = recordsWithBothFields
            .GroupBy(record => record.TenantId)
            .ToDictionary(group => group.Key!, group => group.Select(record => record.UserIdHash).Distinct().ToList());

        hashesByTenant.Should().ContainKey(tenantA);
        hashesByTenant.Should().ContainKey(tenantB);
        hashesByTenant[tenantA].Should().ContainSingle("every record tagged with tenant A's id should carry the same hashed user id");
        hashesByTenant[tenantB].Should().ContainSingle("every record tagged with tenant B's id should carry the same hashed user id");
        hashesByTenant[tenantA].Single().Should().NotBe(
            hashesByTenant[tenantB].Single(), "the two tenants' users must never share a log-visible identity");

        foreach (var record in recordsWithBothFields)
        {
            record.StateText.Should().NotContain(userA);
            record.StateText.Should().NotContain(userB);
        }
    }

    /// <summary>Story 1 scenario 4, on both routes (G4-21-02): a caller whose tenant already
    /// exists, but who has no membership of her own, is refused rather than auto-joined — and
    /// no membership row is created for her (proven through the owner's own member list, never
    /// a direct database read).</summary>
    [Fact]
    public async Task A_different_caller_for_an_existing_tenant_is_refused_on_both_routes_and_creates_no_membership()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tenancyConnectionString = await _database.EnsureStartedAsync(cancellationToken);

        var tenant = Guid.NewGuid().ToString();
        var owner = Canaries.Unique("owner");
        var stranger = Canaries.Unique("stranger");

        await using var factory = ApiTestFactory.Create(
            _issuer, extraConfiguration: [new("ConnectionStrings:tenancy", tenancyConnectionString)]);
        using var client = factory.CreateClient();

        // JIT-provisions the tenant and its Owner membership.
        using (await SendAsync(client, "/api/tenancy/me", _issuer.IssueValidAccessToken(subject: owner, tenantId: tenant), cancellationToken))
        {
        }

        var strangerToken = _issuer.IssueValidAccessToken(subject: stranger, tenantId: tenant);

        foreach (var path in new[] { "/api/tenancy/me", "/api/tenancy/members" })
        {
            using var response = await SendAsync(client, path, strangerToken, cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, path);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            body.Should().NotContain(stranger, path);
            body.Should().NotContain(tenant, path);
        }

        using var membersResponse = await SendAsync(
            client, "/api/tenancy/members", _issuer.IssueValidAccessToken(subject: owner, tenantId: tenant), cancellationToken);
        membersResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var membersDocument = JsonDocument.Parse(await membersResponse.Content.ReadAsStringAsync(cancellationToken));
        var members = membersDocument.RootElement.GetProperty("members").EnumerateArray().ToList();

        members.Should().ContainSingle("the stranger's refused attempt must never have created a membership row");
        members[0].GetProperty("userId").GetString().Should().Be(owner);
    }

    /// <summary>Story 3 scenario 2 (G4-21-02): a Member (not an Owner) cannot list the tenant's
    /// members. The Member row is seeded directly — the flow that would create one through an
    /// invitation is out of scope (#83) — and the 403 carries the framework's own empty-body
    /// Forbid shape (S-2), never the middleware's ProblemDetails shape.</summary>
    [Fact]
    public async Task A_member_with_no_owner_role_cannot_list_the_tenants_members()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tenancyConnectionString = await _database.EnsureStartedAsync(cancellationToken);

        var tenant = Guid.NewGuid().ToString();
        var owner = Canaries.Unique("owner");
        var member = Canaries.Unique("member");

        await using var factory = ApiTestFactory.Create(
            _issuer, extraConfiguration: [new("ConnectionStrings:tenancy", tenancyConnectionString)]);
        using var client = factory.CreateClient();

        using (await SendAsync(client, "/api/tenancy/me", _issuer.IssueValidAccessToken(subject: owner, tenantId: tenant), cancellationToken))
        {
        }

        await SeedMemberAsync(tenancyConnectionString, tenant, member, cancellationToken);

        using var response = await SendAsync(
            client, "/api/tenancy/members", _issuer.IssueValidAccessToken(subject: member, tenantId: tenant), cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        body.Should().BeEmpty("the Tenancy.Owner policy's own Forbid is the framework's bare empty-body shape (S-2), not the middleware's ProblemDetails");
    }

    /// <summary>Story 3 scenario 3 (G4-21-02): a platform admin with no tenant at all cannot
    /// list any tenant's members.</summary>
    [Fact]
    public async Task A_caller_with_no_tenant_cannot_list_any_tenants_members()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tenancyConnectionString = await _database.EnsureStartedAsync(cancellationToken);

        await using var factory = ApiTestFactory.Create(
            _issuer, extraConfiguration: [new("ConnectionStrings:tenancy", tenancyConnectionString)]);
        using var client = factory.CreateClient();

        var token = _issuer.IssueValidAccessToken(tenantId: null);

        using var response = await SendAsync(client, "/api/tenancy/members", token, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        body.Should().BeEmpty("the Tenancy.Owner policy's own Forbid is the framework's bare empty-body shape (S-2)");
    }

    private static async Task<(string ExpectedTenant, HttpResponseMessage Response)> SendMeAsync(
        HttpClient client, string token, string expectedTenant, CancellationToken cancellationToken)
    {
        var response = await SendAsync(client, "/api/tenancy/me", token, cancellationToken);
        return (expectedTenant, response);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, string path, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, cancellationToken);
    }

    /// <summary>Seeds a <see cref="TenantRole.Member"/> row directly, bypassing JIT
    /// provisioning (which only ever assigns <see cref="TenantRole.Owner"/>) — mirrors Story 3
    /// scenario 2's own wording ("seeded directly for this test").</summary>
    private static async Task SeedMemberAsync(
        string tenancyConnectionString, string tenantIdText, string userId, CancellationToken cancellationToken)
    {
        var tenantId = TenantId.Parse(tenantIdText);
        var optionsBuilder = new DbContextOptionsBuilder<TenancyDbContext>();
        TenancyDbContextOptions.Configure(optionsBuilder, tenancyConnectionString);

        await using var db = new TenancyDbContext(
            optionsBuilder.Options, new TestCurrentTenant { Resolution = TenantResolution.For(tenantId) });
        db.Memberships.Add(new Membership(tenantId, userId, TenantRole.Member, NodaTime.SystemClock.Instance.GetCurrentInstant()));
        await db.SaveChangesAsync(cancellationToken);
    }
}
