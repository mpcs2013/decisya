using Decisya.SharedKernel.Tenancy;
using StackExchange.Redis;

namespace Decisya.Bff.Tests;

/// <summary>
/// Issue #231, architecture rule R13 and G1 Story 5, last scenario (two-tenant isolation): two tenants store
/// a value under the same resource name, through <c>TenantKey.ToCacheKey</c>, in a real Redis container. Each
/// reads only its own value, an overwrite stays with its tenant, and a delete for tenant A leaves tenant B's
/// value intact. The keys are the only thing separating the tenants, so this is the behaviour proof that
/// the builder's fixed-length tenant prefix does its job (the rule in <c>Decisya.ArchitectureTests</c> is the
/// proof that nothing builds a key another way).
/// </summary>
[Trait("Category", "Integration")]
public class TenantKeyRedisIsolationTests
{
    private readonly RedisFixture _redisFixture;

    public TenantKeyRedisIsolationTests(RedisFixture redisFixture)
    {
        _redisFixture = redisFixture;
    }

    [Fact]
    public async Task Two_tenants_storing_the_same_resource_name_each_read_only_their_own_value_and_a_delete_for_one_leaves_the_other()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _redisFixture.EnsureStartedAsync(cancellationToken);
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(_redisFixture.ConnectionString);
        var database = multiplexer.GetDatabase();

        var tenantA = TenantId.New();
        var tenantB = TenantId.New();
        var resource = "isolation-" + Guid.NewGuid().ToString("N");
        var keyA = TenantKey.For(tenantA, resource, "profile");
        var keyB = TenantKey.For(tenantB, resource, "profile");

        keyA.ToCacheKey().Should().NotBe(keyB.ToCacheKey(), "the same resource name must not give two tenants the same key");

        // Tenant A stores; tenant B has stored nothing, so the same resource name is empty for B.
        (await database.StringSetAsync(keyA.ToCacheKey(), "value-of-tenant-a")).Should().BeTrue();
        (await database.StringGetAsync(keyB.ToCacheKey())).HasValue.Should().BeFalse("tenant B never reads tenant A's value");

        // Tenant B stores a different value for the same resource name; each reads only its own.
        (await database.StringSetAsync(keyB.ToCacheKey(), "value-of-tenant-b")).Should().BeTrue();
        ((string?)await database.StringGetAsync(keyA.ToCacheKey())).Should().Be("value-of-tenant-a");
        ((string?)await database.StringGetAsync(keyB.ToCacheKey())).Should().Be("value-of-tenant-b");

        // An overwrite for tenant A stays with tenant A.
        (await database.StringSetAsync(keyA.ToCacheKey(), "second-value-of-tenant-a")).Should().BeTrue();
        ((string?)await database.StringGetAsync(keyA.ToCacheKey())).Should().Be("second-value-of-tenant-a");
        ((string?)await database.StringGetAsync(keyB.ToCacheKey())).Should().Be("value-of-tenant-b");

        // A delete for tenant A leaves tenant B's value intact.
        (await database.KeyDeleteAsync(keyA.ToCacheKey())).Should().BeTrue();
        (await database.StringGetAsync(keyA.ToCacheKey())).HasValue.Should().BeFalse();
        ((string?)await database.StringGetAsync(keyB.ToCacheKey())).Should().Be("value-of-tenant-b");

        await database.KeyDeleteAsync(keyB.ToCacheKey());
    }
}
