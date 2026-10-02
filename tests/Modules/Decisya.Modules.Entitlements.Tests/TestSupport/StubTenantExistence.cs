using Decisya.Modules.Tenancy.Contracts;
using Decisya.SharedKernel.Tenancy;

namespace Decisya.Modules.Entitlements.Tests.TestSupport;

/// <summary>
/// The Tenancy existence check as the Entitlements tests see it (issue #25): by default tenants A, B
/// and C exist. Like the real one it throws <see cref="ArgumentException"/> for a resolution that is
/// not of kind <c>Tenant</c>. <see cref="Fault"/> makes it throw; <see cref="Calls"/> records every
/// scope it was asked about.
/// </summary>
internal sealed class StubTenantExistence : ITenantExistence
{
    private readonly List<TenantResolution> _calls = [];

    public HashSet<TenantId> Existing { get; } = [EntitlementsHarness.TenantA, EntitlementsHarness.TenantB, EntitlementsHarness.TenantC];

    /// <summary>When set, <see cref="ExistsAsync"/> throws it.</summary>
    public Exception? Fault { get; set; }

    public IReadOnlyList<TenantResolution> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public Task<bool> ExistsAsync(TenantResolution target, CancellationToken cancellationToken = default)
    {
        if (target.Kind != TenantResolutionKind.Tenant)
        {
            throw new ArgumentException("A tenant existence check needs a resolution of kind Tenant.", nameof(target));
        }

        lock (_calls)
        {
            _calls.Add(target);
        }

        if (Fault is { } fault)
        {
            throw fault;
        }

        return Task.FromResult(Existing.Contains(target.TenantId));
    }
}
