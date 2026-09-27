using System.Reflection;

namespace Decisya.Identity.Tests;

/// <summary>
/// G2: every test class that uses <see cref="KeycloakRealmFixture"/> (via assembly-fixture
/// constructor injection) needs Docker and must carry <c>[Trait("Category", "Integration")]</c>,
/// so ci.yml's unit step (<c>--filter-not-trait "Category=Integration"</c>) excludes it.
/// Mirrors <c>Decisya.AppHost.Tests/AppHostCategoryTraitGuardTests</c>.
/// </summary>
public class IntegrationCategoryTraitGuardTests
{
    [Fact]
    public void Every_class_that_takes_the_Keycloak_realm_fixture_carries_the_Integration_category_trait()
    {
        var assembly = typeof(IntegrationCategoryTraitGuardTests).Assembly;

        var fixtureConsumers = assembly.GetTypes()
            .Where(type => type.IsPublic && type != typeof(IntegrationCategoryTraitGuardTests))
            .Where(UsesKeycloakRealmFixture)
            .ToList();

        // A guard that silently passes because it found nothing to guard is not a guard.
        fixtureConsumers.Should().NotBeEmpty(
            "this project should contain at least one class that consumes KeycloakRealmFixture " +
            "(e.g. RealmConfigurationTests) for this reflection guard to check");

        var classesMissingTheTrait = fixtureConsumers
            .Where(type => !HasIntegrationCategoryTrait(type))
            .Select(type => type.FullName)
            .ToList();

        classesMissingTheTrait.Should().BeEmpty(
            "every class constructed with KeycloakRealmFixture needs Docker, so it must carry " +
            "[Trait(\"Category\", \"Integration\")] to stay excluded from ci.yml's unit step");
    }

    private static bool UsesKeycloakRealmFixture(Type type) =>
        type.GetConstructors()
            .Any(constructor => constructor.GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(KeycloakRealmFixture)));

    private static bool HasIntegrationCategoryTrait(Type type) =>
        type.GetCustomAttributes<TraitAttribute>(inherit: true)
            .Any(trait => trait.Name == "Category" && trait.Value == "Integration");
}
