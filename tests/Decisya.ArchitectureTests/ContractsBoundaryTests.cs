namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #21, G2, ADR-0005: every <see cref="ContractsScope"/> assembly may depend on only
/// <c>Decisya.SharedKernel</c> and other <c>*.Contracts</c> assemblies among Decisya's own
/// assemblies, and never on EF Core, Npgsql, Wolverine, ASP.NET Core or
/// <c>Decisya.SharedKernel.Results</c> (#32 carry).
/// </summary>
[Trait("Category", "Architecture")]
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

    // #231 R8: the banned prefix is the ASSEMBLY name, "Wolverine"; "WolverineFx" is the package id and
    // never the name of an assembly, so the old entry matched nothing. "JasperFx" is Wolverine's core.
    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Npgsql")]
    [InlineData("Wolverine")]
    [InlineData("JasperFx")]
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

    // #231 R8: proves the Wolverine entry above is not vacuous. The real assembly is named "Wolverine", so the
    // prefix matches it, and the package id "WolverineFx" (the old entry) matches no assembly name at all.
    [Fact]
    public void The_Wolverine_ban_matches_the_real_assembly_name_and_a_reference_to_it_would_be_caught()
    {
        var wolverine = typeof(Wolverine.IMessageBus).Assembly.GetName().Name;

        wolverine.Should().Be("Wolverine");
        wolverine!.StartsWith("Wolverine", StringComparison.Ordinal).Should().BeTrue();
        wolverine.StartsWith("WolverineFx", StringComparison.Ordinal).Should().BeFalse("the package id is not an assembly name");
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
