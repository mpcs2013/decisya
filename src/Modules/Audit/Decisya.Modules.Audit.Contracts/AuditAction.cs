namespace Decisya.Modules.Audit.Contracts;

/// <summary>
/// The closed, code-defined set of audited actions (issue #24, G2; G1 Story 3). The Audit
/// module maps each value to its stored code (<c>entitlements.trial.start</c>,
/// <c>entitlements.override.grant</c>, <c>entitlements.override.revoke</c>) and rejects any
/// undefined value before reaching the database. A new action is a reviewed change here.
/// </summary>
public enum AuditAction
{
    /// <summary>A platform admin started the target tenant's one trial. Carries no feature key.</summary>
    EntitlementsTrialStart = 1,

    /// <summary>A platform admin granted (or replaced) a feature override for the target tenant. Carries the feature key.</summary>
    EntitlementsOverrideGrant = 2,

    /// <summary>A platform admin revoked a feature override of the target tenant, whether or not one existed. Carries the feature key.</summary>
    EntitlementsOverrideRevoke = 3,

    /// <summary>A tenant owner created a ledger account (#341, ADR-0013 amendment 1). Carries the account id as subject, no feature key. Appended only by Modules.Ledger.</summary>
    LedgerAccountCreate = 4,

    /// <summary>A tenant owner renamed a ledger account. Carries the account id as subject, no feature key. Appended only by Modules.Ledger.</summary>
    LedgerAccountRename = 5,

    /// <summary>A tenant owner changed a ledger account's currency. Carries the account id as subject, no feature key. Appended only by Modules.Ledger.</summary>
    LedgerAccountCurrencyChange = 6,
}
