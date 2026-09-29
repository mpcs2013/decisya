namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #21, G2, ADR-0005: every <see cref="ContractsScope"/> assembly may depend on only
/// <c>Decisya.SharedKernel</c> and other <c>*.Contracts</c> assemblies among Decisya's own
/// assemblies, and never on EF Core, Npgsql, Wolverine, ASP.NET Core or
/// <c>Decisya.SharedKernel.Results</c> (#32 carry).
/// </summary>
public class ContractsBoundaryTests
{
    [Fact]
    public void Every_Contracts_assembly_depends_on_only_SharedKernel_and_other_Contracts_assemblies_among_Decisya_assemblies()
    {
        var violations = new List<string>();

        foreach (var assembly in ContractsScope.Assemblies)
        {
            var decisyaReferences = assembly.GetReferencedAssemblies()
                .Select(a => a.Name ?? string.Empty)
                .Where(n => n.StartsWith("Decisya.", StringComparison.Ordinal));

            foreach (var reference in decisyaReferences)
            {
                var allowed = reference == "Decisya.SharedKernel" ||
                    (reference.StartsWith("Decisya.Modules.", StringComparison.Ordinal) &&
                        reference.EndsWith(".Contracts", StringComparison.Ordinal));

                if (!allowed)
                {
                    violations.Add($"{assembly.GetName().Name} -> {reference}");
                }
            }
        }

        violations.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Npgsql")]
    [InlineData("WolverineFx")]
    [InlineData("Microsoft.AspNetCore")]
    public void No_Contracts_assembly_depends_on_a_persistence_or_web_framework(string bannedPrefix)
    {
        var violations = new List<string>();

        foreach (var assembly in ContractsScope.Assemblies)
        {
            var offending = assembly.GetReferencedAssemblies()
                .Select(a => a.Name ?? string.Empty)
                .Where(n => n.StartsWith(bannedPrefix, StringComparison.Ordinal));

            violations.AddRange(offending.Select(o => $"{assembly.GetName().Name} -> {o}"));
        }

        violations.Should().BeEmpty();
    }

    [Fact]
    public void No_Contracts_assembly_depends_on_SharedKernel_Results()
    {
        foreach (var assembly in ContractsScope.Assemblies)
        {
            var result = NetArchTest.Rules.Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOn("Decisya.SharedKernel.Results")
                .GetResult();

            result.IsSuccessful.Should().BeTrue();
        }
    }
}
