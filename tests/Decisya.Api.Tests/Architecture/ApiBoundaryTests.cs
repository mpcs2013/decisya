using System.Reflection;
using NetArchTest.Rules;

namespace Decisya.Api.Tests.Architecture;

/// <summary>
/// Hosts get telemetry only through <c>AddServiceDefaults</c>; only
/// <c>Decisya.ServiceDefaults</c> ever references OpenTelemetry directly (#22 adds
/// <c>Decisya.Modules.*</c> to this rule).
/// </summary>
public class ApiBoundaryTests
{
    // G2 (issue #17): Decisya reaches Keycloak only through standard OIDC and JWT, never a
    // vendor client SDK, so the IdP stays replaceable (ADR-0002's option analysis).
    private static readonly string[] KeycloakSdkNamespaces = ["Keycloak", "Keycloak.AuthServices", "FS.Keycloak"];

    private static readonly string[] TestcontainersNamespaces = ["DotNet.Testcontainers", "Testcontainers"];

    private static readonly string[] BoundaryCheckedAssemblyNames =
        ["Decisya.Api", "Decisya.ServiceDefaults", "Decisya.SharedKernel"];

    [Fact]
    public void Decisya_Api_does_not_depend_on_OpenTelemetry_directly()
    {
        var result = Types.InAssembly(typeof(Program).Assembly)
            .ShouldNot()
            .HaveDependencyOn("OpenTelemetry")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Theory]
    [MemberData(nameof(BoundaryCheckedAssemblies))]
    public void No_type_depends_on_a_Keycloak_client_SDK_namespace(string assemblyName)
    {
        var assembly = ResolveAssembly(assemblyName);

        foreach (var keycloakSdkNamespace in KeycloakSdkNamespaces)
        {
            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOn(keycloakSdkNamespace).GetResult();

            result.IsSuccessful.Should().BeTrue(
                $"{assemblyName} must reach Keycloak only through standard OIDC/JWT, never '{keycloakSdkNamespace}': " +
                string.Join(", ", result.FailingTypeNames ?? []));
        }
    }

    [Theory]
    [MemberData(nameof(BoundaryCheckedAssemblies))]
    public void No_type_depends_on_Testcontainers(string assemblyName)
    {
        var assembly = ResolveAssembly(assemblyName);

        foreach (var testcontainersNamespace in TestcontainersNamespaces)
        {
            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOn(testcontainersNamespace).GetResult();

            result.IsSuccessful.Should().BeTrue(
                $"{assemblyName} must not reference Testcontainers (test-only): " +
                string.Join(", ", result.FailingTypeNames ?? []));
        }
    }

    public static IEnumerable<object[]> BoundaryCheckedAssemblies() =>
        BoundaryCheckedAssemblyNames.Select(name => new object[] { name });

    private static Assembly ResolveAssembly(string assemblyName) =>
        string.Equals(assemblyName, typeof(Program).Assembly.GetName().Name, StringComparison.Ordinal)
            ? typeof(Program).Assembly
            : Assembly.Load(assemblyName);
}
