using System.Reflection;
using Decisya.Modules.Entitlements.Contracts;
using Decisya.Modules.Entitlements.Domain;

namespace Decisya.Modules.Entitlements.Tests;

/// <summary>
/// Issue #26, Story 4 (ADR-0008 amendment 1): <c>FeatureKeys.All</c> is the list the capability manifest
/// publishes, so it must equal the keys the class declares and the keys the plan catalog knows. A key added
/// to one place only fails here instead of silently missing from (or inventing a key in) the manifest.
/// </summary>
[Trait("Category", "Unit")]
public sealed class FeatureKeysAllTests
{
    private static List<FeatureKey> DeclaredKeys() =>
        typeof(FeatureKeys)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(FeatureKey))
            .Select(property => (FeatureKey)property.GetValue(null)!)
            .ToList();

    [Fact]
    public void FeatureKeys_All_has_every_public_static_FeatureKey_property_once()
    {
        var declared = DeclaredKeys();

        declared.Should().NotBeEmpty();
        FeatureKeys.All.Should().OnlyHaveUniqueItems();
        FeatureKeys.All.Select(key => key.Value).Should().BeEquivalentTo(declared.Select(key => key.Value));
    }

    [Fact]
    public void FeatureKeys_All_lists_the_keys_in_declaration_order()
    {
        var declared = typeof(FeatureKeys)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(FeatureKey))
            .OrderBy(property => property.MetadataToken)
            .Select(property => ((FeatureKey)property.GetValue(null)!).Value);

        FeatureKeys.All.Select(key => key.Value).Should().Equal(declared);
    }

    [Fact]
    public void FeatureKeys_All_as_a_set_equals_the_keys_the_default_plan_catalog_knows()
    {
        var known = typeof(PlanCatalog)
            .GetField("_known", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(PlanCatalog.Default) as IEnumerable<FeatureKey>;
        known.Should().NotBeNull("PlanCatalog must keep its known-key set in the private field _known (update this test if it is renamed)");

        known!.Select(key => key.Value).Should().BeEquivalentTo(FeatureKeys.All.Select(key => key.Value));
        foreach (var key in FeatureKeys.All)
        {
            PlanCatalog.Default.IsKnown(key).Should().BeTrue(key.Value);
        }
    }
}
