using Decisya.SharedKernel.Results;

namespace Decisya.Modules.Entitlements.Application;

/// <summary>
/// The admin commands' expected failures (G2 "Fixed interface"). Every message is a fixed
/// string: never the reason, the command or the entity (G3 R-3).
/// </summary>
internal static class EntitlementsErrors
{
    public static DomainError Forbidden { get; } = DomainError.New(
        "entitlements.forbidden", "An entitlement admin command was refused: the ambient caller is not a tenant-less admin caller.", ErrorCategory.Forbidden);

    public static DomainError ActorUnknown { get; } = DomainError.New(
        "entitlements.actor_unknown", "An entitlement admin command was refused: the caller has no validated user id.", ErrorCategory.Forbidden);

    public static DomainError TenantInvalid { get; } = DomainError.New(
        "entitlements.tenant_invalid", "The command's target tenant id is not initialized.", ErrorCategory.Validation);

    public static DomainError FeatureUnknown { get; } = DomainError.New(
        "entitlements.feature_unknown", "The feature key is not known to the plan catalog.", ErrorCategory.Validation);

    public static DomainError ReasonInvalid { get; } = DomainError.New(
        "entitlements.reason_invalid", "The override reason must be 1 to 500 characters after trimming.", ErrorCategory.Validation);

    public static DomainError ExpiryNotInFuture { get; } = DomainError.New(
        "entitlements.expiry_not_in_future", "The override expiry must be later than now.", ErrorCategory.Validation);

    public static DomainError TrialAlreadyUsed { get; } = DomainError.New(
        "entitlements.trial_already_used", "The tenant has already used its one trial.", ErrorCategory.Conflict);
}
