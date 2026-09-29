using System.Reflection;
using Decisya.Api.Tests.Authentication;

namespace Decisya.Api.Tests.Architecture;

/// <summary>
/// G2 (mirrors <c>Decisya.Identity.Tests/IntegrationCategoryTraitGuardTests</c>): every class
/// that takes <see cref="KeycloakApiFixture"/> or <see cref="TenancyDatabaseFixture"/> (via
/// assembly-fixture constructor injection) needs Docker and must carry
/// <c>[Trait("Category", "Integration")]</c>, so ci.yml's unit step excludes it.
/// </summary>
public class IntegrationCategoryTraitGuardTests
{
    private static readonly Type[] DockerNeedingFixtureTypes = [typeof(KeycloakApiFixture), typeof(TenancyDatabaseFixture)];

    [Fact]
    public void Every_class_that_takes_a_Docker_needing_fixture_carries_the_Integration_category_trait()
    {
        var assembly = typeof(IntegrationCategoryTraitGuardTests).Assembly;

        var fixtureConsumers = assembly.GetTypes()
            .Where(type => type.IsPublic && type != typeof(IntegrationCategoryTraitGuardTests))
            .Where(UsesADockerNeedingFixture)
            .ToList();

        fixtureConsumers.Should().NotBeEmpty(
            "this project should contain at least one class that consumes a Docker-needing fixture for this guard to check");

        var classesMissingTheTrait = fixtureConsumers
            .Where(type => !HasIntegrationCategoryTrait(type))
            .Select(type => type.FullName)
            .ToList();

        classesMissingTheTrait.Should().BeEmpty(
            "every class constructed with a Docker-needing fixture must carry " +
            "[Trait(\"Category\", \"Integration\")] to stay excluded from ci.yml's unit step");
    }

    private static bool UsesADockerNeedingFixture(Type type) =>
        type.GetConstructors()
            .Any(constructor => constructor.GetParameters()
                .Any(parameter => DockerNeedingFixtureTypes.Contains(parameter.ParameterType)));

    private static bool HasIntegrationCategoryTrait(Type type) =>
        type.GetCustomAttributes<TraitAttribute>(inherit: true)
            .Any(trait => trait.Name == "Category" && trait.Value == "Integration");
}
