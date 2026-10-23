namespace Decisya.ArchitectureTests;

/// <summary>
/// #164 G2 (NetArchTest rules table): the Ostium packages are confined to <c>Decisya.Api</c> (rule in
/// <c>ApiBoundaryTests</c>) and test projects. No module, no <c>*.Contracts</c> assembly and no
/// persistence assembly may reference an <c>Ostium.*</c> assembly. (SharedKernel and ServiceDefaults hold
/// no reference either: <c>OstiumSupplyChainTests</c> proves that only <c>Decisya.Api.csproj</c> under
/// <c>src/</c> names an <c>Ostium.*</c> package.)
/// </summary>
[Trait("Category", "Architecture")]
public class OstiumBoundaryTests
{
    [Fact]
    public void Modules_and_Contracts_assemblies_do_not_reference_an_Ostium_assembly()
    {
        var scoped = ArchitectureScope.Assemblies.Concat(ContractsScope.Assemblies).ToList();
        scoped.Should().NotBeEmpty();

        var violations = scoped
            .SelectMany(assembly => assembly.GetReferencedAssemblies()
                .Where(reference => reference.Name?.StartsWith("Ostium", StringComparison.Ordinal) == true)
                .Select(reference => $"{assembly.GetName().Name} -> {reference.Name}"))
            .ToList();

        violations.Should().BeEmpty();
    }
}
