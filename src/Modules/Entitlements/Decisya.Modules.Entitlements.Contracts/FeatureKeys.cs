namespace Decisya.Modules.Entitlements.Contracts;

/// <summary>
/// The platform's feature keys (issue #23, G1 Q1; G2). Which plan includes which key is the
/// Entitlements module's internal <c>PlanCatalog</c>; a consumer names a key only through
/// this class, so a typo is a compile error rather than a silent denial.
/// </summary>
public static class FeatureKeys
{
    /// <summary>Recording and reading ledger transactions. Plans: Free, Pro.</summary>
    public static FeatureKey LedgerTransactions { get; } = FeatureKey.Create("ledger.transactions");

    /// <summary>Forecast scenarios, a Phase 3 capability registered ahead of time. Plan: Pro only.</summary>
    public static FeatureKey ForecastingScenarios { get; } = FeatureKey.Create("forecasting.scenarios");

    /// <summary>
    /// Every key above, in declaration order (issue #26, G2; ADR-0008 amendment 1). The capability
    /// manifest (<c>GET /api/capabilities</c>) lists exactly these keys, so a key added here appears
    /// in the manifest with no SPA change. Declared after the keys it lists (static initialisation
    /// runs in textual order). Tests pin it to the public static <see cref="FeatureKey"/> properties
    /// of this class and to the plan catalog's known set.
    /// </summary>
    public static IReadOnlyList<FeatureKey> All { get; } = [LedgerTransactions, ForecastingScenarios];
}
