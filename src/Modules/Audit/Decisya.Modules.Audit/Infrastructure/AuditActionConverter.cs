using Decisya.Modules.Audit.Contracts;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Decisya.Modules.Audit.Infrastructure;

/// <summary>
/// Stores an <see cref="AuditAction"/> as its stable code through an explicit map, never the enum
/// name (G2): renaming a member can never change what is in the table. An undefined value throws.
/// </summary>
internal sealed class AuditActionConverter : ValueConverter<AuditAction, string>
{
    internal const string TrialStart = "entitlements.trial.start";
    internal const string OverrideGrant = "entitlements.override.grant";
    internal const string OverrideRevoke = "entitlements.override.revoke";
    internal const string LedgerAccountCreate = "ledger.account.create";
    internal const string LedgerAccountRename = "ledger.account.rename";
    internal const string LedgerAccountCurrencyChange = "ledger.account.currency_change";

    public AuditActionConverter()
        : base(action => ToCode(action), code => FromCode(code))
    {
    }

    internal static string ToCode(AuditAction action) => action switch
    {
        AuditAction.EntitlementsTrialStart => TrialStart,
        AuditAction.EntitlementsOverrideGrant => OverrideGrant,
        AuditAction.EntitlementsOverrideRevoke => OverrideRevoke,
        AuditAction.LedgerAccountCreate => LedgerAccountCreate,
        AuditAction.LedgerAccountRename => LedgerAccountRename,
        AuditAction.LedgerAccountCurrencyChange => LedgerAccountCurrencyChange,
        _ => throw new ArgumentOutOfRangeException(nameof(action), "The audit action is not defined."),
    };

    internal static AuditAction FromCode(string code) => code switch
    {
        TrialStart => AuditAction.EntitlementsTrialStart,
        OverrideGrant => AuditAction.EntitlementsOverrideGrant,
        OverrideRevoke => AuditAction.EntitlementsOverrideRevoke,
        LedgerAccountCreate => AuditAction.LedgerAccountCreate,
        LedgerAccountRename => AuditAction.LedgerAccountRename,
        LedgerAccountCurrencyChange => AuditAction.LedgerAccountCurrencyChange,
        _ => throw new InvalidOperationException("The stored audit action code is not recognised."),
    };
}
