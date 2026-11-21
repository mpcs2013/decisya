namespace Decisya.Modules.Entitlements.Contracts;

/// <summary>
/// The names of the entitlement authorization policies (issue #341, G2 D4; ADR-0008): one policy per
/// <see cref="FeatureKey"/>, named <c>Entitlement:&lt;key&gt;</c>. The Api registers them; a module's
/// endpoint group requires one by name through <see cref="NameFor"/>.
/// </summary>
public static class EntitlementPolicies
{
    /// <summary>The prefix every entitlement policy name carries.</summary>
    public const string Prefix = "Entitlement:";

    /// <summary>The policy name for <paramref name="feature"/>.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="feature"/> is <c>default(FeatureKey)</c>.</exception>
    public static string NameFor(FeatureKey feature) => Prefix + feature.Value;
}
