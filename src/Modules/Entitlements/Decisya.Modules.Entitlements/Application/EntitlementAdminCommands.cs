using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Entitlements.Contracts.Admin;
using Decisya.SharedKernel.Results;
using Decisya.SharedKernel.Tenancy;
using NodaTime;

namespace Decisya.Modules.Entitlements.Application;

/// <summary>
/// The public admin surface of the module (issue #25, G2 D3; ADR-0012 amendment 1 point 5). Not
/// <c>[AllowCrossTenant]</c>: it mints nothing, holds no context and writes no audit record. It
/// builds the internal command record, calls the handler (which checks the caller, validates,
/// checks the target tenant and audits in one transaction) and maps the <see cref="Result"/> to the
/// contract.
/// </summary>
internal sealed class EntitlementAdminCommands(
    StartTrialHandler startTrial,
    GrantOverrideHandler grantOverride,
    RevokeOverrideHandler revokeOverride) : IEntitlementAdminCommands
{
    public async Task<EntitlementAdminResult> StartTrialAsync(TenantId tenantId, CancellationToken cancellationToken = default) =>
        Map(await startTrial.HandleAsync(new StartTrial(tenantId), cancellationToken).ConfigureAwait(false));

    public async Task<EntitlementAdminResult> GrantOverrideAsync(
        TenantId tenantId, FeatureKey feature, string reason, Instant? expiresAt, CancellationToken cancellationToken = default) =>
        Map(await grantOverride.HandleAsync(new GrantOverride(tenantId, feature, reason, expiresAt), cancellationToken).ConfigureAwait(false));

    public async Task<EntitlementAdminResult> RevokeOverrideAsync(
        TenantId tenantId, FeatureKey feature, CancellationToken cancellationToken = default) =>
        Map(await revokeOverride.HandleAsync(new RevokeOverride(tenantId, feature), cancellationToken).ConfigureAwait(false));

    internal static EntitlementAdminResult Map(Result result)
    {
        if (result.IsSuccess)
        {
            return EntitlementAdminResult.Succeeded;
        }

        var error = result.Error;
        var status = error.Category switch
        {
            ErrorCategory.Validation => EntitlementAdminStatus.Invalid,
            ErrorCategory.NotFound => EntitlementAdminStatus.NotFound,
            ErrorCategory.Conflict => EntitlementAdminStatus.Conflict,
            ErrorCategory.Forbidden => EntitlementAdminStatus.Forbidden,
            _ => throw new InvalidOperationException("An entitlement admin command failed with an unexpected error category."),
        };

        return EntitlementAdminResult.Failed(status, error.Code);
    }
}
