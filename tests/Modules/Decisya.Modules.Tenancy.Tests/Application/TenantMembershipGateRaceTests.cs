using Decisya.Modules.Tenancy.Application;
using Decisya.Modules.Tenancy.Domain;
using Decisya.Modules.Tenancy.Infrastructure;
using Decisya.Modules.Tenancy.Tests.TestSupport;
using Decisya.SharedKernel.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;

namespace Decisya.Modules.Tenancy.Tests.Application;

/// <summary>
/// NFR-32 / G4-21-03's race red tests: N = 16 parallel <c>EnsureAsync</c> calls, each against
/// its own <c>TenancyDbContext</c> instance (separate contexts, per G3), for one brand-new
/// <c>tenant_id</c>. Against a real Postgres database — this is exactly the concurrency
/// behaviour a mocked <c>DbContext</c> could never prove.
/// </summary>
/// <remarks>
/// Previously flaky (issue #21 G4 evidence): an earlier build of
/// <c>TenantMembershipGate.EnsureAsync</c> checked "own membership?" before "tenant exists?",
/// two separate, non-atomic statements. Under Postgres's READ COMMITTED isolation, a losing
/// call whose own membership check ran <em>before</em> the winner committed, and whose
/// tenant-existence check ran <em>after</em>, could observe "I have no membership yet" followed
/// by "the tenant already exists" and wrongly return <c>Refused</c> — even for the one shared
/// <c>userId</c> this repro uses, contradicting G1 Story 1 and G3 G4-21-03's own red-test spec
/// ("every call returns Member or Provisioned"). Fixed by checking the tenant first (see
/// <c>TenantMembershipGate</c>'s own remarks on ordering): once this test's own re-read of the
/// race sees a result, this file's own assertions below still pin the contract in case of a
/// future regression.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class TenantMembershipGateRaceTests(PostgresFixture pg)
{
    private const int RaceCallCount = 16;

    /// <summary>NFR-32: "N = 16 parallel EnsureAsync calls for one new t and one user give exactly one Tenant row and one Owner membership, and every call returns Member or Provisioned."</summary>
    [Fact]
    public async Task N16_parallel_first_sign_in_requests_for_one_new_tenant_and_one_user_create_exactly_one_tenant_and_one_Owner_membership()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var t = TenantId.New();
        const string userId = "dev-alice";
        var db = await pg.CreateDatabaseAsync<TenancyDbContext>(cancellationToken);

        var tasks = Enumerable.Range(0, RaceCallCount)
            .Select(_ => RunGateAsync(db, t, userId, cancellationToken))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(
            r => r == TenantMembershipResult.Member || r == TenantMembershipResult.Provisioned,
            "no call for the winning, only ever-seen user may ever be Refused");
        results.Should().Contain(TenantMembershipResult.Provisioned, "exactly one call must have won the race and created the tenant");

        await using var verify = db.CreateContext(t);
        (await verify.Tenants.CountAsync(cancellationToken)).Should().Be(1);
        var membership = await verify.Memberships.SingleAsync(cancellationToken);
        membership.UserId.Should().Be(userId);
        membership.Role.Should().Be(TenantRole.Owner);
    }

    /// <summary>NFR-32 continued: "The same with two users: exactly one Owner. Every call made by the other user returns Refused, and that user has no membership row."</summary>
    [Fact]
    public async Task N16_parallel_first_sign_in_requests_for_one_new_tenant_and_two_users_produce_exactly_one_Owner_and_refuse_every_call_of_the_other_user()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var t = TenantId.New();
        const string userA = "dev-alice";
        const string userB = "dev-bob";
        var db = await pg.CreateDatabaseAsync<TenancyDbContext>(cancellationToken);

        var callsPerUser = RaceCallCount / 2;
        var tasks = Enumerable.Range(0, callsPerUser)
            .SelectMany(_ => new[]
            {
                RunGateTaggedAsync(db, t, userA, cancellationToken),
                RunGateTaggedAsync(db, t, userB, cancellationToken),
            })
            .ToArray();

        var results = await Task.WhenAll(tasks);

        var resultsByUser = results.ToLookup(r => r.UserId, r => r.Result);
        var winners = new[] { userA, userB }
            .Where(user => resultsByUser[user].Any(r => r != TenantMembershipResult.Refused))
            .ToList();

        winners.Should().HaveCount(1, "exactly one of the two users must win the brand-new tenant");
        var winner = winners[0];
        var loser = winner == userA ? userB : userA;

        resultsByUser[winner].Should().OnlyContain(r => r == TenantMembershipResult.Member || r == TenantMembershipResult.Provisioned);
        resultsByUser[loser].Should().OnlyContain(r => r == TenantMembershipResult.Refused);

        await using var verify = db.CreateContext(t);
        (await verify.Tenants.CountAsync(cancellationToken)).Should().Be(1);
        var membership = await verify.Memberships.SingleAsync(cancellationToken);
        membership.UserId.Should().Be(winner);
        membership.Role.Should().Be(TenantRole.Owner);
        (await verify.Memberships.AnyAsync(m => m.UserId == loser, cancellationToken)).Should().BeFalse();
    }

    private static async Task<TenantMembershipResult> RunGateAsync(
        PostgresTestDatabase<TenancyDbContext> db, TenantId t, string userId, CancellationToken cancellationToken)
    {
        await using var ctx = db.CreateContext(t);
        var gate = new TenantMembershipGate(
            ctx,
            new TestCurrentTenant { Resolution = TenantResolution.For(t) },
            new StubCurrentCaller(userId),
            SystemClock.Instance,
            NullLogger<TenantMembershipGate>.Instance);

        return await gate.EnsureAsync(cancellationToken);
    }

    private static async Task<(string UserId, TenantMembershipResult Result)> RunGateTaggedAsync(
        PostgresTestDatabase<TenancyDbContext> db, TenantId t, string userId, CancellationToken cancellationToken)
    {
        var result = await RunGateAsync(db, t, userId, cancellationToken);
        return (userId, result);
    }
}
