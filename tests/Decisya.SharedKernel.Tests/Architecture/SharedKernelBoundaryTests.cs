using System.Reflection;
using Decisya.SharedKernel.Results;
using Decisya.SharedKernel.Tenancy;
using NetArchTest.Rules;

namespace Decisya.SharedKernel.Tests.Architecture;

/// <summary>
/// The NetArchTest rules from the G2 architecture note's "NetArchTest rules to add" table
/// (issue #32). #22 moves these into <c>Decisya.ArchitectureTests</c> and widens them
/// repo-wide.
/// </summary>
public class SharedKernelBoundaryTests
{
    private static readonly string[] AllowedAssemblyReferences =
    [
        "System",
        "netstandard",
        "NodaTime",
        "Microsoft.Extensions.DependencyInjection.Abstractions",
    ];

    [Fact]
    public void Tenancy_types_only_depend_on_System_and_Tenancy()
    {
        var result = Types.InAssembly(typeof(TenantId).Assembly)
            .That().ResideInNamespace("Decisya.SharedKernel.Tenancy")
            .Should().OnlyHaveDependenciesOn("System", "Decisya.SharedKernel.Tenancy")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(FailureDetail(result));
    }

    [Fact]
    public void Results_types_only_depend_on_System_and_Results()
    {
        var result = Types.InAssembly(typeof(DomainError).Assembly)
            .That().ResideInNamespace("Decisya.SharedKernel.Results")
            .Should().OnlyHaveDependenciesOn("System", "Decisya.SharedKernel.Results")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(FailureDetail(result));
    }

    [Fact]
    public void Results_types_do_not_depend_on_HTTP_or_ASPNETCORE_assemblies()
    {
        var result = Types.InAssembly(typeof(DomainError).Assembly)
            .That().ResideInNamespace("Decisya.SharedKernel.Results")
            .ShouldNot().HaveDependencyOnAny("System.Net", "Microsoft.AspNetCore")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(FailureDetail(result));
    }

    [Fact]
    public void SharedKernels_referenced_assemblies_stay_on_the_allow_list()
    {
        var referenced = typeof(TenantId).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        referenced.Should().OnlyContain(
            name => AllowedAssemblyReferences.Any(allowed => name == allowed || name.StartsWith(allowed + ".", StringComparison.Ordinal)),
            "SharedKernel must reference only System.*, netstandard, NodaTime and Microsoft.Extensions.DependencyInjection.Abstractions; actual: {0}",
            string.Join(", ", referenced));
    }

    // --- G1 Story 4 scenario 7: neither Result nor Result<T> converts implicitly to bool ---

    [Fact]
    public void Result_declares_no_conversion_to_bool_and_no_operator_true_or_false()
    {
        AssertNoBoolConversion(typeof(Result));
    }

    [Fact]
    public void ResultOfT_declares_no_conversion_to_bool_and_no_operator_true_or_false()
    {
        AssertNoBoolConversion(typeof(Result<int>));
    }

    private static void AssertNoBoolConversion(Type type)
    {
        var operators = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name is "op_Implicit" or "op_Explicit" or "op_True" or "op_False")
            .ToArray();

        operators.Should().NotContain(m => m.Name == "op_True" || m.Name == "op_False");
        operators.Should().NotContain(m => m.ReturnType == typeof(bool));
    }

    private static string FailureDetail(NetArchTest.Rules.TestResult result) =>
        string.Join(", ", result.FailingTypeNames ?? []);
}
