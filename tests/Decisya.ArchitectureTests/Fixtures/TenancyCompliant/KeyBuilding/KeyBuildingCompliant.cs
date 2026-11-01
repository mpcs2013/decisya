using System.Security.Claims;
using Decisya.SharedKernel.Tenancy;
using Microsoft.Extensions.Caching.Distributed;

namespace Decisya.ArchitectureTests.Fixtures.TenancyCompliant.KeyBuilding;

// R10 green fixtures (#231, G1 Story 5): every key goes through TenantKey, and a session-keyed store that
// holds no tenant passes when it is on the exemption list (as the Redis session ticket store is).

/// <summary>Every method that touches the cache builds its key with <c>TenantKey</c>.</summary>
public sealed class TenantKeyedCache(IDistributedCache cache)
{
    public Task<string?> GetAsync(TenantId tenant, string resource, CancellationToken cancellationToken) =>
        cache.GetStringAsync(TenantKey.For(tenant, resource).ToCacheKey(), cancellationToken);

    public Task SetAsync(TenantKey key, string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return cache.SetStringAsync(key.ToCacheKey(), value, cancellationToken);
    }

    public Task RemoveAsync(TenantKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return cache.RemoveAsync(key.ToCacheKey(), cancellationToken);
    }
}

/// <summary>Keyed by session, holds no tenant, reads the <c>sid</c> claim only: exempt by exact name.</summary>
public sealed class SessionKeyedCache(IDistributedCache cache)
{
    public Task<string?> GetAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return cache.GetStringAsync("session:" + principal.FindFirst("sid")?.Value, cancellationToken);
    }
}
