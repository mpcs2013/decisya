using System.Reflection;
using Decisya.TestInfrastructure;

namespace Decisya.Modules.Ledger.Tests;

/// <summary>
/// testcontainers skill: every test class that takes the shared <see cref="PostgresFixture"/> (by constructor
/// injection) needs Docker and must carry <c>[Trait("Category", "Integration")]</c>, so the unit step and the
/// pre-push hook (<c>--filter-not-trait "Category=Integration"</c>) exclude it. Mirrors
/// <c>Decisya.Identity.Tests/IntegrationCategoryTraitGuardTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public class IntegrationCategoryTraitGuardTests
{
    [Fact]
    public void Every_class_that_takes_the_Postgres_fixture_carries_the_Integration_category_trait()
    {
        var assembly = typeof(IntegrationCategoryTraitGuardTests).Assembly;

        var fixtureConsumers = assembly.GetTypes()
            .Where(type => type.IsPublic && type != typeof(IntegrationCategoryTraitGuardTests))
            .Where(UsesPostgresFixture)
            .ToList();

        // A guard that silently passes because it found nothing to guard is not a guard.
        fixtureConsumers.Should().NotBeEmpty(
            "this project contains the limit race, isolation, audit and schema classes this guard checks");

        var classesMissingTheTrait = fixtureConsumers
            .Where(type => !HasIntegrationCategoryTrait(type))
            .Select(type => type.FullName)
            .ToList();

        classesMissingTheTrait.Should().BeEmpty(
            "every class constructed with PostgresFixture needs Docker, so it must carry " +
            "[Trait(\"Category\", \"Integration\")] to stay excluded from the unit step");
    }

    private static bool UsesPostgresFixture(Type type) =>
        type.GetConstructors()
            .Any(constructor => constructor.GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(PostgresFixture)));

    private static bool HasIntegrationCategoryTrait(Type type) =>
        type.GetCustomAttributes<TraitAttribute>(inherit: true)
            .Any(trait => trait.Name == "Category" && trait.Value == "Integration");
}
