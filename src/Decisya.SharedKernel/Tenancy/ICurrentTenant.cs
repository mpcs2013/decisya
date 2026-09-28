namespace Decisya.SharedKernel.Tenancy;

/// <summary>
/// The ambient tenant for the current request or job. <c>Decisya.Infrastructure.Persistence.TenantDbContext</c>
/// reads <see cref="Resolution"/> once per query execution and once per save, never a value
/// captured earlier, so a single implementation instance can never leak one tenant's rows
/// into a query issued under another tenant.
/// </summary>
/// <remarks>
/// #22 ships this interface only. The production implementation — registered scoped, set
/// exactly once per scope, populated from <c>CallerIdentity.TenantId</c> by host middleware
/// that turns a malformed claim into an HTTP 403 before any handler runs — is #21's
/// responsibility (G1 open question 1; G3 boundary B-1). Tests in #22 supply an ambient
/// tenant directly, without a host or a real login.
/// </remarks>
public interface ICurrentTenant
{
    /// <summary>The current tenant resolution. Read fresh on every access.</summary>
    TenantResolution Resolution { get; }
}
