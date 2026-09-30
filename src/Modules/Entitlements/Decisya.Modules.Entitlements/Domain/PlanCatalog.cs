using Decisya.Modules.Entitlements.Contracts;

namespace Decisya.Modules.Entitlements.Domain;

/// <summary>
/// Which plan includes which feature key, in code (issue #23, G1 Q1; G2 D1). The internal
/// constructor is the only test seam: the real catalog has no second Pro-only key.
/// </summary>
internal sealed class PlanCatalog
{
    private readonly Dictionary<PlanId, HashSet<FeatureKey>> _plans;
    private readonly HashSet<FeatureKey> _known;

    internal PlanCatalog(IReadOnlyDictionary<PlanId, IReadOnlyCollection<FeatureKey>> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);

        _plans = plans.ToDictionary(p => p.Key, p => p.Value.ToHashSet());
        _known = _plans.Values.SelectMany(keys => keys).ToHashSet();
        _known.Remove(default);
    }

    /// <summary>Free has <c>ledger.transactions</c>; Pro has that and <c>forecasting.scenarios</c>.</summary>
    public static PlanCatalog Default { get; } = new(new Dictionary<PlanId, IReadOnlyCollection<FeatureKey>>
    {
        [PlanId.Free] = [FeatureKeys.LedgerTransactions],
        [PlanId.Pro] = [FeatureKeys.LedgerTransactions, FeatureKeys.ForecastingScenarios],
    });

    /// <summary>Listed by any plan. <see langword="false"/> for <c>default(FeatureKey)</c>.</summary>
    public bool IsKnown(FeatureKey feature) => feature.IsInitialized && _known.Contains(feature);

    public bool Includes(PlanId plan, FeatureKey feature) =>
        feature.IsInitialized && _plans.TryGetValue(plan, out var keys) && keys.Contains(feature);
}
