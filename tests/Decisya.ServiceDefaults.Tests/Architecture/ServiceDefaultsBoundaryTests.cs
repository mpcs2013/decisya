using System.Reflection;
using Decisya.ServiceDefaults.Logging;
using Decisya.SharedKernel.Observability;
using NetArchTest.Rules;

namespace Decisya.ServiceDefaults.Tests.Architecture;

/// <summary>
/// The NetArchTest rules from the G2 architecture note's "NetArchTest rules to add" table.
/// #22 moves these into <c>Decisya.ArchitectureTests</c> and widens them repo-wide; here
/// they cover the three assemblies issue #15 touches.
/// </summary>
public class ServiceDefaultsBoundaryTests
{
    private static readonly string[] VendorNamespaces =
    [
        "Microsoft.ApplicationInsights",
        "Azure.Monitor",
        "Datadog",
        "NewRelic",
        "Elastic.Apm",
        "Sentry",
        "Honeycomb",
        "Dynatrace",
    ];

    [Fact]
    public void SharedKernel_does_not_depend_on_web_logging_or_telemetry_assemblies()
    {
        var result = Types.InAssembly(typeof(SensitiveAttribute).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.AspNetCore",
                "Microsoft.Extensions.Logging",
                "OpenTelemetry",
                "Decisya.ServiceDefaults",
                "Decisya.Api",
                "Decisya.AppHost")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(FailureDetail(result));
    }

    [Fact]
    public void ServiceDefaults_does_not_depend_on_Api_AppHost_or_modules()
    {
        var result = Types.InAssembly(typeof(SensitiveDataMaskingProcessor).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny("Decisya.Api", "Decisya.AppHost", "Decisya.Modules")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(FailureDetail(result));
    }

    [Fact]
    public void No_type_depends_on_a_vendor_observability_namespace()
    {
        foreach (var assembly in new[] { typeof(SensitiveDataMaskingProcessor).Assembly, typeof(SensitiveAttribute).Assembly })
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOnAny(VendorNamespaces)
                .GetResult();

            result.IsSuccessful.Should().BeTrue(FailureDetail(result));
        }
    }

    [Fact]
    public void Only_ServiceDefaults_depends_on_OpenTelemetry_among_the_assemblies_this_issue_touches()
    {
        var result = Types.InAssembly(typeof(SensitiveAttribute).Assembly)
            .ShouldNot()
            .HaveDependencyOn("OpenTelemetry")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(FailureDetail(result));
    }

    [Fact]
    public void SensitiveAttribute_is_declared_exactly_once_in_SharedKernel_Observability_and_is_sealed()
    {
        var candidates = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("Decisya.", StringComparison.Ordinal) == true)
            .SelectMany(SafeGetTypes)
            .Where(t => t.Name == nameof(SensitiveAttribute))
            .ToArray();

        candidates.Should().ContainSingle();
        candidates[0].Namespace.Should().Be(typeof(SensitiveAttribute).Namespace);
        candidates[0].IsSealed.Should().BeTrue();
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    private static string FailureDetail(NetArchTest.Rules.TestResult result) =>
        string.Join(", ", result.FailingTypeNames ?? []);
}
