namespace Decisya.Modules.Entitlements.Contracts.Admin;

/// <summary>
/// The stable error codes of the Entitlements admin commands (issues #23, #24 and #25, G2 "Fixed
/// interface"). The module's internal <c>EntitlementsErrors</c> uses these constants, so the codes
/// a client sees and the codes the handlers return have one source. <see cref="Forbidden"/> and
/// <see cref="ActorUnknown"/> are never written to an HTTP body: a 403 carries no code.
/// </summary>
public static class EntitlementAdminErrorCodes
{
    /// <summary>The caller is not a tenant-less platform admin (handler precondition).</summary>
    public const string Forbidden = "entitlements.forbidden";

    /// <summary>The caller has no validated user id.</summary>
    public const string ActorUnknown = "entitlements.actor_unknown";

    /// <summary>The target tenant id is missing, unparseable or the all-zero GUID.</summary>
    public const string TenantInvalid = "entitlements.tenant_invalid";

    /// <summary>The feature key is malformed, or (for a grant) not in the plan catalog.</summary>
    public const string FeatureUnknown = "entitlements.feature_unknown";

    /// <summary>The override reason is empty after trimming, or longer than 500 characters.</summary>
    public const string ReasonInvalid = "entitlements.reason_invalid";

    /// <summary>The override expiry is not later than now.</summary>
    public const string ExpiryNotInFuture = "entitlements.expiry_not_in_future";

    /// <summary>The tenant has already used its one trial.</summary>
    public const string TrialAlreadyUsed = "entitlements.trial_already_used";

    /// <summary>No tenant with the target id exists (#25, S-5).</summary>
    public const string TenantNotFound = "entitlements.tenant_not_found";
}
