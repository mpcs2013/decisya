using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Decisya.Api.Tests.Authentication;
using Decisya.Modules.Tenancy.Domain;
using Decisya.Modules.Tenancy.Infrastructure;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Decisya.Api.Tests.Errors;

/// <summary>
/// Issue #21, G2's own design note ("#21 has no update path of its own, so G5 raises a real
/// conflict through a test-only endpoint... a detached Update of another tenant's Membership,
/// which the #22 concurrency token turns into zero rows"); G3 G4-21-04's own red test. This
/// complements the unit-level <see cref="ConcurrencyConflictExceptionHandlerTests"/> (a
/// hand-built exception, invoked directly) with the real thing: a genuine
/// <see cref="DbUpdateConcurrencyException"/>, raised by EF Core itself against a real Postgres
/// database, reported to the caller as a generic 404 over an actual HTTP round trip through
/// <c>Decisya.Api</c>'s own pipeline (authentication, caller context, exception handler).
/// </summary>
[Trait("Category", "Integration")]
public sealed class ConcurrencyConflictHttpRoundTripTests : IDisposable
{
    private readonly TenancyDatabaseFixture _database;
    private readonly TestTokenIssuer _issuer = new();

    public ConcurrencyConflictHttpRoundTripTests(TenancyDatabaseFixture database)
    {
        _database = database;
    }

    public void Dispose()
    {
        _issuer.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Story 5 scenario 1 (G1), proven over a real HTTP round trip (G2, G4-21-04, NFR-33).</summary>
    [Fact]
    public async Task A_concurrency_conflict_during_a_Tenancy_write_is_reported_as_a_generic_404()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tenancyConnectionString = await _database.EnsureStartedAsync(cancellationToken);

        var victimTenant = Guid.NewGuid().ToString();
        var victimOwner = Canaries.Unique("victim-owner");
        var victimMembershipId = await SeedOwnerMembershipAsync(tenancyConnectionString, victimTenant, victimOwner, cancellationToken);

        var canary = Canaries.Unique("concurrency-conflict");
        var capturingProvider = new CapturingLoggerProvider();
        await using var factory = ApiTestFactory.Create(
            _issuer,
            extraConfiguration: [new("ConnectionStrings:tenancy", tenancyConnectionString)],
            testEndpointsCanary: canary,
            configureLogging: logging => logging.AddProvider(capturingProvider));
        using var client = factory.CreateClient();

        var attackerTenant = Guid.NewGuid().ToString();
        var attackerToken = _issuer.IssueValidAccessToken(subject: Canaries.Unique("attacker"), tenantId: attackerTenant);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{TestOnlyEndpointsStartupFilter.ConcurrencyConflictPath}?membershipId={victimMembershipId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", attackerToken);

        using var response = await client.SendAsync(request, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.CacheControl?.ToString().Should().Contain("no-store");

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        using var body = JsonDocument.Parse(text);
        var propertyNames = body.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        propertyNames.Should().BeEquivalentTo(
            ["type", "title", "status", "traceId"],
            "the body must carry no entity name, column name or row value from either side of the conflict");
        body.RootElement.GetProperty("status").GetInt32().Should().Be(404);
        body.RootElement.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty();

        text.Should().NotContain(victimTenant, "the victim tenant's id must never reach the client");
        text.Should().NotContain(victimOwner, "the victim's own user id must never reach the client");
        text.Should().NotContain(nameof(Membership), "no entity name may reach the client");
        text.Should().NotContain("tenant_id", "no column name may reach the client");
        text.Should().NotContain("user_id", "no column name may reach the client");
        text.Should().NotContain("memberships", "no table name may reach the client");

        capturingProvider.Records.Should().Contain(
            record => record.Contains(nameof(DbUpdateConcurrencyException)),
            "the conflict's full detail must still reach the structured log");
    }

    private static async Task<Guid> SeedOwnerMembershipAsync(
        string tenancyConnectionString, string tenantIdText, string userId, CancellationToken cancellationToken)
    {
        var tenantId = TenantId.Parse(tenantIdText);
        var optionsBuilder = new DbContextOptionsBuilder<TenancyDbContext>();
        TenancyDbContextOptions.Configure(optionsBuilder, tenancyConnectionString);

        await using var db = new TenancyDbContext(
            optionsBuilder.Options, new TestCurrentTenant { Resolution = TenantResolution.For(tenantId) });
        var now = SystemClock.Instance.GetCurrentInstant();
        db.Tenants.Add(new Tenant(tenantId, now));
        var membership = new Membership(tenantId, userId, TenantRole.Owner, now);
        db.Memberships.Add(membership);
        await db.SaveChangesAsync(cancellationToken);
        return membership.Id;
    }
}
