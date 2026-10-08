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
    public void Only_types_in_Decisya_Bff_Proxy_depend_on_Yarp_ReverseProxy()
    {
        var result = Types.InAssembly(BffAssembly)
            .That().DoNotResideInNamespace("Decisya.Bff.Proxy")
            .ShouldNot().HaveDependencyOn("Yarp.ReverseProxy")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Only_types_in_Decisya_Bff_Security_depend_on_Microsoft_IdentityModel_JsonWebTokens()
    {
        // #19 G2: the BFF never parses the access token (NetArchTest rule); LogoutTokenValidator
        // (Decisya.Bff.Security) is the one reader of a JWT, for back-channel logout.
        var result = Types.InAssembly(BffAssembly)
            .That().DoNotResideInNamespace("Decisya.Bff.Security")
            .ShouldNot().HaveDependencyOn("Microsoft.IdentityModel.JsonWebTokens")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Decisya_Bff_csproj_has_exactly_one_ProjectReference_and_only_the_expected_Yarp_and_ServiceDiscovery_packages()
    {
        var csprojPath = RepoPaths.Find(Path.Combine("src", "Decisya.Bff", "Decisya.Bff.csproj"));
        var content = File.ReadAllText(csprojPath);

        var projectReferenceCount = System.Text.RegularExpressions.Regex.Count(content, "<ProjectReference\\b");
        projectReferenceCount.Should().Be(1, "Decisya.Bff should reference only Decisya.ServiceDefaults");
        content.Should().Contain("Decisya.ServiceDefaults.csproj");

        // #19 G2: #18's "no Yarp.* package" rule is replaced by this one — YARP forwarding is
        // #19's own scope now, but only these two package ids may appear.
        var packageReferenceIds = System.Text.RegularExpressions.Regex
            .Matches(content, "<PackageReference Include=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .Where(id => id.Contains("Yarp", StringComparison.Ordinal) || id.Contains("ServiceDiscovery", StringComparison.Ordinal))
            .ToList();

        packageReferenceIds.Should().BeEquivalentTo(["Yarp.ReverseProxy", "Microsoft.Extensions.ServiceDiscovery.Yarp"]);
    }

    // ---- #122 G2 "NetArchTest rules to add" (rules 1 to 5) ----

    [Theory]
    [InlineData("System.Threading.RateLimiting")]
    [InlineData("Microsoft.AspNetCore.RateLimiting")]
    public void Only_types_in_Decisya_Bff_RateLimiting_depend_on_the_rate_limiter_namespaces(string limiterNamespace)
    {
        var result = Types.InAssembly(BffAssembly)
            .That().DoNotResideInNamespace("Decisya.Bff.RateLimiting")
            .ShouldNot().HaveDependencyOn(limiterNamespace)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Theory]
    [InlineData("Microsoft.AspNetCore.DataProtection.KeyManagement")]
    [InlineData("Microsoft.AspNetCore.DataProtection.XmlEncryption")]
    [InlineData("Microsoft.AspNetCore.DataProtection.Repositories")]
    public void Only_types_in_Decisya_Bff_KeyRing_depend_on_the_key_management_namespaces(string dataProtectionNamespace)
    {
        var result = Types.InAssembly(BffAssembly)
            .That().DoNotResideInNamespace("Decisya.Bff.KeyRing")
            .ShouldNot().HaveDependencyOn(dataProtectionNamespace)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Only_types_in_Decisya_Bff_KeyRing_implement_IXmlRepository()
    {
        var result = Types.InAssembly(BffAssembly)
            .That().ImplementInterface(typeof(Microsoft.AspNetCore.DataProtection.Repositories.IXmlRepository))
            .Should().ResideInNamespace("Decisya.Bff.KeyRing")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void No_type_implements_IXmlEncryptor_or_IXmlDecryptor()
    {
        var encryptors = Types.InAssembly(BffAssembly)
            .That().ImplementInterface(typeof(Microsoft.AspNetCore.DataProtection.XmlEncryption.IXmlEncryptor)).GetTypes();
        var decryptors = Types.InAssembly(BffAssembly)
            .That().ImplementInterface(typeof(Microsoft.AspNetCore.DataProtection.XmlEncryption.IXmlDecryptor)).GetTypes();

        encryptors.Should().BeEmpty("a hand-rolled encryptor is not acceptable (G2 D10)");
        decryptors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Microsoft.AspNetCore.DataProtection.StackExchangeRedis")]
    [InlineData("Microsoft.AspNetCore.DataProtection.EntityFrameworkCore")]
    public void The_key_ring_is_never_stored_in_Redis_or_a_database(string packageNamespace)
    {
        AssertNoDependency(BffAssembly, packageNamespace);

        var csproj = File.ReadAllText(RepoPaths.Find(Path.Combine("src", "Decisya.Bff", "Decisya.Bff.csproj")));
        csproj.Should().NotContain(packageNamespace);
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
