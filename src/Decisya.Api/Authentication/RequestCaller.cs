using Decisya.SharedKernel.Tenancy;

namespace Decisya.Api.Authentication;

/// <summary>
/// The production, request-scoped <see cref="ICurrentTenant"/> and <see cref="ICurrentCaller"/>
/// (issue #21, G2 D5; G3 G4-21-01, closes #22 B-1). Registered exactly once, as
/// <c>AddScoped&lt;RequestCaller&gt;()</c>; both interfaces are forwarded to the very same
/// scoped instance (<c>ApiAuthenticationBuilderExtensions.AddApiAuthentication</c>), so a
/// request's tenant and caller can never diverge from one another mid-request. Populated
/// exactly once per request or job, by <see cref="CallerContextMiddleware"/>, from the
/// validated principal's own claims.
/// </summary>
internal sealed class RequestCaller : ICurrentTenant, ICurrentCaller
{
    private bool _isSet;
    private string? _userId;

    /// <summary>
    /// Before <see cref="Set"/> runs, this is <c>default(TenantResolution)</c> —
    /// <see cref="TenantResolution.Invalid"/> — the same fail-closed default
    /// <see cref="ICurrentTenant"/> documents: a query that somehow reaches
    /// <c>TenantDbContext</c> before the caller-context middleware has run fails closed
    /// instead of quietly behaving like "no tenant" or a specific tenant.
    /// </summary>
    public TenantResolution Resolution { get; private set; }

    /// <summary>
    /// False before <see cref="Set"/> runs (and never throws), then exactly what <see cref="Set"/>
    /// assigned: the validated role <b>and</b> a tenant-less resolution (issue #25, G3 G4-25-01).
    /// Assigned nowhere else. Authorization code combines it with
    /// <c>Resolution.Kind == None</c> and never reads it alone.
    /// </summary>
    public bool IsPlatformAdmin { get; private set; }

    /// <summary>
    /// The validated <c>sub</c> claim, once <see cref="Set"/> has run.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Set"/> has not run yet for this request or job.
    /// </exception>
    public string UserId => _userId ?? throw new InvalidOperationException(
        "RequestCaller.UserId was read before CallerContextMiddleware set it for this request or job.");

    /// <summary>
    /// Populates the current tenant and caller, exactly once per request or job.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Set"/> was already called once on this instance — including a second call
    /// with identical values (G3 G4-21-01, T-02): the current tenant and caller must be
    /// resolved exactly once, never re-resolved mid-request.
    /// </exception>
    internal void Set(TenantResolution resolution, string userId, bool isPlatformAdmin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        if (_isSet)
        {
            throw new InvalidOperationException(
                "RequestCaller.Set was already called for this request or job; the current " +
                "tenant and caller must be resolved exactly once.");
        }

        Resolution = resolution;
        _userId = userId;

        // The setter repeats the "no tenant" half itself, so the fact can never be true for a
        // tenant caller, whatever its one caller passes.
        IsPlatformAdmin = isPlatformAdmin && resolution.Kind == TenantResolutionKind.None;
        _isSet = true;
    }
}
