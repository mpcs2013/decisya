using Decisya.Modules.Entitlements.Contracts.Admin;
using Decisya.SharedKernel.Results;

namespace Decisya.Modules.Entitlements.Application;

/// <summary>
/// The admin commands' expected failures (G2 "Fixed interface"). Every message is a fixed
/// string: never the reason, the command or the entity (G3 R-3).
/// </summary>
internal static class EntitlementsErrors
{
    public static DomainError Forbidden { get; } = DomainError.New(
        EntitlementAdminErrorCodes.Forbidden, "An entitlement admin command was refused: the caller is not a tenant-less platform admin.", ErrorCategory.Forbidden);

    public static DomainError ActorUnknown { get; } = DomainError.New(
        EntitlementAdminErrorCodes.ActorUnknown, "An entitlement admin command was refused: the caller has no validated user id.", ErrorCategory.Forbidden);

    public static DomainError TenantInvalid { get; } = DomainError.New(
        EntitlementAdminErrorCodes.TenantInvalid, "The command's target tenant id is not initialized.", ErrorCategory.Validation);

    public static DomainError FeatureUnknown { get; } = DomainError.New(
        EntitlementAdminErrorCodes.FeatureUnknown, "The feature key is not known to the plan catalog.", ErrorCategory.Validation);

    public static DomainError ReasonInvalid { get; } = DomainError.New(
        EntitlementAdminErrorCodes.ReasonInvalid, "The override reason must be 1 to 500 characters after trimming.", ErrorCategory.Validation);

    public static DomainError ExpiryNotInFuture { get; } = DomainError.New(
        EntitlementAdminErrorCodes.ExpiryNotInFuture, "The override expiry must be later than now.", ErrorCategory.Validation);

    public static DomainError TrialAlreadyUsed { get; } = DomainError.New(
        EntitlementAdminErrorCodes.TrialAlreadyUsed, "The tenant has already used its one trial.", ErrorCategory.Conflict);

    public static DomainError TenantNotFound { get; } = DomainError.New(
        EntitlementAdminErrorCodes.TenantNotFound, "The target tenant does not exist.", ErrorCategory.NotFound);
}
