namespace Decisya.Modules.Ledger.Domain;

/// <summary>
/// The account limit, as one fixed value in code (issue #341, G2 D2; AC-02-5). This is the only place the
/// value appears. No configuration key, option, plan, trial or override reads or changes it. EP-03 adds the
/// one Pro key check next to its single read in <c>CreateAccountHandler</c>; it adds no second mechanism.
/// </summary>
internal static class AccountLimits
{
    /// <summary>The most accounts one space holds.</summary>
    internal const int PerSpace = 2;
}
