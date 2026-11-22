using System.Reflection;
using NetArchTest.Rules;

namespace Decisya.Modules.Ledger.Tests;

[Trait("Category", "Architecture")]
public class LedgerModuleBoundaryTests
{
    private static readonly Assembly ModuleAssembly = typeof(LedgerModule).Assembly;

    [Fact]
    public void The_Ledger_module_references_no_other_modules_implementation()
    {
        var otherModules = ModuleAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("Decisya.Modules.", StringComparison.Ordinal))
            .Where(n => !n.EndsWith(".Contracts", StringComparison.Ordinal))
            .Where(n => n != ModuleAssembly.GetName().Name);

        otherModules.Should().BeEmpty();
    }

    [Fact]
    public void The_Ledger_module_and_its_contracts_reference_no_vendor_AI_SDK()
    {
        string[] vendorPrefixes = ["Anthropic", "OpenAI", "Azure.AI", "Google.Cloud.AIPlatform", "Google.GenAI", "Mistral", "Cohere", "LLamaSharp"];
        var contractsAssembly = typeof(Decisya.Modules.Ledger.Contracts.LedgerErrorCodes).Assembly;

        var referenced = ModuleAssembly.GetReferencedAssemblies()
            .Concat(contractsAssembly.GetReferencedAssemblies())
            .Select(a => a.Name ?? string.Empty)
            .Where(n => vendorPrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase)));

        referenced.Should().BeEmpty();
    }

    [Fact]
    public void The_Ledger_module_never_reads_the_system_clock_directly()
    {
        var result = Types.InAssembly(ModuleAssembly)
            .Should()
            .NotHaveDependencyOn("NodaTime.SystemClock")
            .GetResult();

        result.IsSuccessful.Should().BeTrue();
    }
}
