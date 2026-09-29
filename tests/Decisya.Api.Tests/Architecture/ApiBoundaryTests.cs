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

    // G2 (issue #18, 0.06 BFF): the BFF is a separate host that talks to Decisya.Api over
    // HTTP; none of the API-side assemblies may reach back into it.
    [Theory]
    [MemberData(nameof(BoundaryCheckedAssemblies))]
    public void No_type_depends_on_Decisya_Bff(string assemblyName)
    {
        var assembly = ResolveAssembly(assemblyName);

        var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOn("Decisya.Bff").GetResult();

        result.IsSuccessful.Should().BeTrue(
            $"{assemblyName} must not depend on Decisya.Bff (the BFF talks to the API over HTTP, never in-process): " +
            string.Join(", ", result.FailingTypeNames ?? []));
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

    // #20 G2: Program sits in the global namespace and never references JwtBearer or
    // Microsoft.IdentityModel types directly; only Decisya.Api.Authentication may.
    [Fact]
    public void Only_Decisya_Api_Authentication_depends_on_JwtBearer_or_IdentityModel()
    {
        foreach (var dependency in new[] { "Microsoft.AspNetCore.Authentication.JwtBearer", "Microsoft.IdentityModel" })
        {
            var result = Types.InAssembly(typeof(Program).Assembly)
                .That().DoNotResideInNamespace("Decisya.Api.Authentication")
                .ShouldNot().HaveDependencyOn(dependency)
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                $"only Decisya.Api.Authentication may depend on '{dependency}': " + string.Join(", ", result.FailingTypeNames ?? []));
        }
    }

    // #20 G2 D4: forwarded headers stay off until 0.16; no type may even reference the
    // middleware that would trust them.
    [Fact]
    public void No_type_depends_on_ForwardedHeaders()
    {
        foreach (var dependency in new[] { "Microsoft.AspNetCore.HttpOverrides", "Microsoft.AspNetCore.Builder.ForwardedHeadersExtensions" })
        {
            var result = Types.InAssembly(typeof(Program).Assembly).ShouldNot().HaveDependencyOn(dependency).GetResult();

            result.IsSuccessful.Should().BeTrue(
                $"Decisya.Api must not depend on '{dependency}' (D4): " + string.Join(", ", result.FailingTypeNames ?? []));
        }
    }

    // #20 G2: the handler uses JsonWebTokenHandler; System.IdentityModel.Tokens.Jwt (the older
    // JwtSecurityTokenHandler family) must never be reachable.
    [Fact]
    public void No_type_depends_on_the_legacy_JwtSecurityTokenHandler_namespace()
    {
        var result = Types.InAssembly(typeof(Program).Assembly)
            .ShouldNot().HaveDependencyOn("System.IdentityModel.Tokens.Jwt")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    // #20 G2 static rule, updated by #21 (G2's "Static rule, update" row): Decisya.Api.csproj
    // carries exactly two ProjectReferences (Decisya.ServiceDefaults, Decisya.Modules.Tenancy)
    // and exactly one PackageReference (the JwtBearer handler) — EF Core reaches Decisya.Api
    // only transitively, through Decisya.Modules.Tenancy, never as a direct package reference.
    [Fact]
    public void Decisya_Api_csproj_has_exactly_the_ServiceDefaults_and_Tenancy_ProjectReferences_and_only_the_JwtBearer_package()
    {
        var csprojPath = RepoPaths.Find(Path.Combine("src", "Decisya.Api", "Decisya.Api.csproj"));
        var content = File.ReadAllText(csprojPath);

        var projectReferenceCount = System.Text.RegularExpressions.Regex.Count(content, "<ProjectReference\\b");
        projectReferenceCount.Should().Be(2, "Decisya.Api should reference only Decisya.ServiceDefaults and Decisya.Modules.Tenancy");
        content.Should().Contain("Decisya.ServiceDefaults.csproj");
        content.Should().Contain("Decisya.Modules.Tenancy.csproj");

        var packageReferenceIds = System.Text.RegularExpressions.Regex
            .Matches(content, "<PackageReference Include=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToList();

        packageReferenceIds.Should().BeEquivalentTo(["Microsoft.AspNetCore.Authentication.JwtBearer"]);
    }

    // #20 G2 static rule (T-11): neither IdentityModel PII/security-artifact logging flag ever
    // appears anywhere under src/, so an exception message never carries a claim or a token.
    [Fact]
    public void No_file_under_src_enables_IdentityModel_PII_or_security_artifact_logging()
    {
        var srcRoot = RepoPaths.Find("src");
        var offendingFiles = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("ShowPII", StringComparison.Ordinal)
                || File.ReadAllText(path).Contains("LogCompleteSecurityArtifact", StringComparison.Ordinal))
            .ToList();

        offendingFiles.Should().BeEmpty(string.Join(", ", offendingFiles));
    }

    public static IEnumerable<object[]> BoundaryCheckedAssemblies() =>
        BoundaryCheckedAssemblyNames.Select(name => new object[] { name });

    private static Assembly ResolveAssembly(string assemblyName) =>
        string.Equals(assemblyName, typeof(Program).Assembly.GetName().Name, StringComparison.Ordinal)
            ? typeof(Program).Assembly
            : Assembly.Load(assemblyName);
}
