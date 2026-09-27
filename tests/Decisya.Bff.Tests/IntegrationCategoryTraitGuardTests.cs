using System.Reflection;

namespace Decisya.Bff.Tests;

/// <summary>
/// Every test class that takes <see cref="KeycloakBffFixture"/> or <see cref="RedisFixture"/>
/// via assembly-fixture constructor injection needs Docker and must carry
/// <c>[Trait("Category", "Integration")]</c>, so ci.yml's unit step
/// (<c>--filter-not-trait "Category=Integration"</c>) excludes it. Mirrors
/// <c>Decisya.Identity.Tests/IntegrationCategoryTraitGuardTests</c>.
/// </summary>
public class IntegrationCategoryTraitGuardTests
{
    [Fact]
    public void Every_class_that_takes_a_container_fixture_carries_the_Integration_category_trait()
    {
        var assembly = typeof(IntegrationCategoryTraitGuardTests).Assembly;

        var fixtureConsumers = assembly.GetTypes()
            .Where(type => type.IsPublic && type != typeof(IntegrationCategoryTraitGuardTests))
            .Where(UsesAContainerFixture)
            .ToList();

        fixtureConsumers.Should().NotBeEmpty(
            "this project should contain at least one class that consumes a container fixture " +
            "for this reflection guard to check");

        var classesMissingTheTrait = fixtureConsumers
            .Where(type => !HasIntegrationCategoryTrait(type))
            .Select(type => type.FullName)
            .ToList();

        classesMissingTheTrait.Should().BeEmpty(
            "every class constructed with a container fixture needs Docker, so it must carry " +
            "[Trait(\"Category\", \"Integration\")] to stay excluded from ci.yml's unit step");
    }

    private static bool UsesAContainerFixture(Type type) =>
        type.GetConstructors().Any(constructor => constructor.GetParameters().Any(parameter =>
            parameter.ParameterType == typeof(KeycloakBffFixture) || parameter.ParameterType == typeof(RedisFixture)));

    private static bool HasIntegrationCategoryTrait(Type type) =>
        type.GetCustomAttributes<TraitAttribute>(inherit: true)
            .Any(trait => trait.Name == "Category" && trait.Value == "Integration");
}
