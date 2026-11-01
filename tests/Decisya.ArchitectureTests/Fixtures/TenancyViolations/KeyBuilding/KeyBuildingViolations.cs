using System.Security.Claims;
using Decisya.SharedKernel.Tenancy;
using Microsoft.Extensions.Caching.Distributed;

namespace Decisya.ArchitectureTests.Fixtures.TenancyViolations.KeyBuilding;

// R10 red fixtures (#231, G1 Story 5; G3 G4-231-05). Each uses an IDistributedCache, one of the key sinks, and
// each breaks one clause of the rule. None of them runs; the IL is what the rule reads.

/// <summary>A key built by hand from <c>TenantId.Value</c>.</summary>
public sealed class HandBuiltKeyCache(IDistributedCache cache)
{
    public Task<string?> GetAsync(TenantId tenant, string name, CancellationToken cancellationToken) =>
        cache.GetStringAsync(tenant.Value.ToString("D") + ":" + name, cancellationToken);
}

/// <summary>A key built by interpolating a <c>TenantId</c> (a generic <c>AppendFormatted&lt;TenantId&gt;</c> call).</summary>
public sealed class InterpolatedKeyCache(IDistributedCache cache)
{
    public Task<string?> GetAsync(TenantId tenant, string name, CancellationToken cancellationToken) =>
        cache.GetStringAsync($"{tenant}:{name}", cancellationToken);
}

/// <summary>A key built from a <c>TenantResolution</c>'s text.</summary>
public sealed class ResolutionTextKeyCache(IDistributedCache cache)
{
    public Task<string?> GetAsync(TenantResolution resolution, string name, CancellationToken cancellationToken) =>
        cache.GetStringAsync(resolution.ToString() + ":" + name, cancellationToken);
}

/// <summary>A sink with a plain string key: what the first storage client would look like without the builder.</summary>
public sealed class PlainStringKeyCache(IDistributedCache cache)
{
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
        cache.GetStringAsync(key, cancellationToken);
}

/// <summary>
/// One compliant method and one hand-built key (G3 G4-231-05 item 3): the rule checks each method, so the
/// failure must name <c>GetRawAsync</c> and never <c>GetForAsync</c>.
/// </summary>
public sealed class MixedKeyCache(IDistributedCache cache)
{
    public Task<string?> GetForAsync(TenantKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return cache.GetStringAsync(key.ToCacheKey(), cancellationToken);
    }

    public Task<string?> GetRawAsync(string key, CancellationToken cancellationToken) =>
        cache.GetStringAsync(key, cancellationToken);
}

/// <summary>Builds its key with <c>TenantKey</c> but feeds it from raw claim text (a <c>System.Security.Claims</c> member).</summary>
public sealed class ClaimFedKeyCache(IDistributedCache cache)
{
    public Task<string?> GetAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var tenant = TenantId.Parse(principal.FindFirst("sub")?.Value ?? string.Empty);
        return cache.GetStringAsync(TenantKey.For(tenant, "profile").ToCacheKey(), cancellationToken);
    }
}

/// <summary>A compliant key method next to tenant text elsewhere in the same type.</summary>
public sealed class TenantTextBesideKeyCache(IDistributedCache cache)
{
    public Task<string?> GetAsync(TenantKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return cache.GetStringAsync(key.ToCacheKey(), cancellationToken);
    }

    public static string Describe(TenantId tenant) => tenant.ToString();
}

/// <summary>Exempt in the tests by its exact name: its nested type counts as it does (Marco, option A).</summary>
public sealed class NestedExemptOuter
{
    public sealed class Inner(IDistributedCache cache)
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
            cache.GetStringAsync(key, cancellationToken);
    }
}

/// <summary>Not exempt: its nested type with a plain-string sink is still red.</summary>
public sealed class NestedPlainOuter
{
    public sealed class Inner(IDistributedCache cache)
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
            cache.GetStringAsync(key, cancellationToken);
    }
}

/// <summary>
/// On the exemption list in the tests, yet it reads the <c>tenant_id</c> claim: an exempt type may read
/// claims, but never that one (G3 T-16).
/// </summary>
public sealed class ExemptReadsTenantClaim(IDistributedCache cache)
{
    public Task<string?> GetAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return cache.GetStringAsync(principal.FindFirst("tenant_id")?.Value ?? "none", cancellationToken);
    }
}

/// <summary>On the exemption list in the tests, yet it references tenant text.</summary>
public sealed class ExemptReadsTenantText(IDistributedCache cache)
{
    public Task<string?> GetAsync(TenantId tenant, CancellationToken cancellationToken) =>
        cache.GetStringAsync(tenant.Value.ToString("D"), cancellationToken);
}
