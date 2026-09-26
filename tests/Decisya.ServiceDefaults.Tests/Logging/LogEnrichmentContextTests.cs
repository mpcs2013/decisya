using Decisya.ServiceDefaults.Logging;

namespace Decisya.ServiceDefaults.Tests.Logging;

/// <summary>G4-15-28: only the hash is stored, nesting restores the previous values, and
/// two concurrent flows never see each other's values.</summary>
public class LogEnrichmentContextTests
{
    private static LogEnrichmentContext CreateContext() =>
        new(new UserIdHasher(Convert.FromBase64String(Canaries.HashKey())));

    [Fact]
    public void Is_null_before_any_Begin_call()
    {
        var context = CreateContext();

        context.TenantId.Should().BeNull();
        context.UserIdHash.Should().BeNull();
    }

    [Fact]
    public void Begin_stores_the_tenant_and_a_hash_of_the_user_id_never_the_raw_id()
    {
        var context = CreateContext();
        var userId = Canaries.Unique("user-id");

        using (context.Begin("tenant-1", userId))
        {
            context.TenantId.Should().Be("tenant-1");
            context.UserIdHash.Should().NotBeNullOrEmpty();
            context.UserIdHash.Should().NotContain(userId);
        }
    }

    [Fact]
    public void Disposing_the_handle_restores_the_previous_values()
    {
        var context = CreateContext();

        using (context.Begin("outer", "outer-user"))
        {
            using (context.Begin("inner", "inner-user"))
            {
                context.TenantId.Should().Be("inner");
            }

            context.TenantId.Should().Be("outer");
        }

        context.TenantId.Should().BeNull();
    }

    [Fact]
    public async Task Two_concurrent_flows_never_see_each_others_values()
    {
        var context = CreateContext();

        async Task<string?> RunAsync(string tenantId)
        {
            using (context.Begin(tenantId, $"{tenantId}-user"))
            {
                await Task.Delay(20);
                return context.TenantId;
            }
        }

        var first = RunAsync("tenant-a");
        var second = RunAsync("tenant-b");
        var results = await Task.WhenAll(first, second);

        results.Should().BeEquivalentTo(["tenant-a", "tenant-b"]);
    }
}
