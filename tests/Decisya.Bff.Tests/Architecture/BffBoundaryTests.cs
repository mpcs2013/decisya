using System.Reflection;
using NetArchTest.Rules;

namespace Decisya.Bff.Tests.Architecture;

/// <summary>G2's NetArchTest rules for <c>Decisya.Bff</c>.</summary>
public class BffBoundaryTests
{
    private static readonly string[] KeycloakSdkNamespaces = ["Keycloak", "Keycloak.AuthServices", "FS.Keycloak"];

    private static readonly string[] TestcontainersNamespaces = ["DotNet.Testcontainers", "Testcontainers"];

    private static Assembly BffAssembly => typeof(Program).Assembly;

    [Fact]
    public void No_type_depends_on_Decisya_Api_or_Decisya_Modules()
    {
        AssertNoDependency(BffAssembly, "Decisya.Api");
        AssertNoDependency(BffAssembly, "Decisya.Modules");
    }

    [Fact]
    public void No_type_depends_on_OpenTelemetry_directly()
    {
        AssertNoDependency(BffAssembly, "OpenTelemetry");
    }

    [Theory]
    [MemberData(nameof(KeycloakNamespaces))]
    public void No_type_depends_on_a_Keycloak_SDK_namespace(string keycloakSdkNamespace)
    {
        AssertNoDependency(BffAssembly, keycloakSdkNamespace);
    }

    [Theory]
    [MemberData(nameof(TestcontainersNamespacesData))]
    public void No_type_depends_on_Testcontainers(string testcontainersNamespace)
    {
        AssertNoDependency(BffAssembly, testcontainersNamespace);
    }

    [Fact]
    public void Only_types_in_Decisya_Bff_Session_depend_on_StackExchange_Redis()
    {
        var result = Types.InAssembly(BffAssembly)
            .That().DoNotResideInNamespace("Decisya.Bff.Session")
            .ShouldNot().HaveDependencyOn("StackExchange.Redis")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void No_type_depends_on_Microsoft_Extensions_Caching()
    {
        AssertNoDependency(BffAssembly, "Microsoft.Extensions.Caching");
    }

    [Fact]
    public void Decisya_Bff_csproj_has_exactly_one_ProjectReference_and_no_Yarp_package()
    {
        var csprojPath = RepoPaths.Find(Path.Combine("src", "Decisya.Bff", "Decisya.Bff.csproj"));
        var content = File.ReadAllText(csprojPath);

        var projectReferenceCount = System.Text.RegularExpressions.Regex.Count(content, "<ProjectReference\\b");
        projectReferenceCount.Should().Be(1, "Decisya.Bff should reference only Decisya.ServiceDefaults");
        content.Should().Contain("Decisya.ServiceDefaults.csproj");

        content.Should().NotContain("Yarp.", "YARP forwarding belongs to #19, not #18");
    }

    public static IEnumerable<object[]> KeycloakNamespaces() => KeycloakSdkNamespaces.Select(name => new object[] { name });

    public static IEnumerable<object[]> TestcontainersNamespacesData() => TestcontainersNamespaces.Select(name => new object[] { name });

    private static void AssertNoDependency(Assembly assembly, string dependency)
    {
        var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOn(dependency).GetResult();

        result.IsSuccessful.Should().BeTrue(
            $"Decisya.Bff must not depend on '{dependency}': " + string.Join(", ", result.FailingTypeNames ?? []));
    }
}
