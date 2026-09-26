using System.Reflection;

namespace Decisya.AppHost.Tests;

/// <summary>
/// Issue #15 (CI fix, G6 I-1): every other test class in this project needs DCP and the
/// Aspire CLI bundle (ADR-0010) and carries [Trait("Category", "AppHost")] so ci.yml's unit
/// step (`--filter-not-trait "Category=AppHost"`) excludes them; they run on Marco's host
/// instead. That left this project with zero tests in CI's unit step, and Microsoft.Testing
/// .Platform exits 8 when a project runs zero tests.
///
/// This class carries no Category trait on purpose: it is the one test in this project that
/// runs both in CI and on the host. Fixing the CI step's flags would only hide the next
/// occurrence, so instead this is a reflection guard over the trait convention itself
/// (xunit.v3 has no assembly-level AssemblyTraitAttribute to apply it once — see
/// AssemblyInfo.cs): it proves every other public test class in this assembly still carries
/// [Trait("Category", "AppHost")], so a future DCP-dependent test cannot slip into CI
/// unnoticed by missing the trait.
/// </summary>
public class AppHostCategoryTraitGuardTests
{
    [Fact]
    public void Every_other_test_class_in_this_assembly_carries_the_AppHost_category_trait()
    {
        var assembly = typeof(AppHostCategoryTraitGuardTests).Assembly;

        var testClasses = assembly.GetTypes()
            .Where(type => type.IsPublic && type != typeof(AppHostCategoryTraitGuardTests))
            .Where(HasFactOrTheoryMethod)
            .ToList();

        // A guard that silently passes because it found nothing to guard is not a guard.
        testClasses.Should().NotBeEmpty(
            "this project should still contain at least one AppHost-only test class " +
            "(e.g. AppHostResourceTests) for this reflection guard to check");

        var classesMissingTheTrait = testClasses
            .Where(type => !HasAppHostCategoryTrait(type))
            .Select(type => type.FullName)
            .ToList();

        classesMissingTheTrait.Should().BeEmpty(
            "every test class in Decisya.AppHost.Tests other than this guard needs DCP and " +
            "the Aspire CLI bundle, so it must carry [Trait(\"Category\", \"AppHost\")] to " +
            "stay excluded from ci.yml's unit step and run only on Marco's host (ADR-0010)");
    }

    private static bool HasFactOrTheoryMethod(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        return type.GetMethods(flags)
            .Any(method => method.GetCustomAttributes(inherit: true)
                .Any(attribute => attribute is FactAttribute));
    }

    private static bool HasAppHostCategoryTrait(Type type)
    {
        return type.GetCustomAttributes<TraitAttribute>(inherit: true)
            .Any(trait => trait.Name == "Category" && trait.Value == "AppHost");
    }
}
