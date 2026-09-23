using System.Reflection;
using NetArchTest.Rules;

namespace Decisya.Modules.__Name__.Tests;

[Trait("Category", "Architecture")]
public class __Name__ModuleBoundaryTests
{
    private static readonly Assembly ModuleAssembly = typeof(__Name__Module).Assembly;

    [Fact]
    public void The___Name___module_references_no_other_modules_implementation()
    {
        var otherModules = ModuleAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("Decisya.Modules.", StringComparison.Ordinal))
            .Where(n => !n.EndsWith(".Contracts", StringComparison.Ordinal))
            .Where(n => n != ModuleAssembly.GetName().Name);

        otherModules.Should().BeEmpty();
    }

    [Fact]
    public void The___Name___module_never_reads_the_system_clock_directly()
    {
        var result = Types.InAssembly(ModuleAssembly)
            .Should()
            .NotHaveDependencyOn("NodaTime.SystemClock")
            .GetResult();

        result.IsSuccessful.Should().BeTrue();
    }
}
