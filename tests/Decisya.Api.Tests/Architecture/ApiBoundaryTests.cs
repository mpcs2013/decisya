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

    // #20 G2 static rule, updated by #21, #23, #24 and #25 (G2's "Static rule, update" rows):
    // Decisya.Api.csproj carries exactly five ProjectReferences (Decisya.ServiceDefaults,
    // Decisya.Modules.Tenancy, Decisya.Modules.Entitlements, Decisya.Modules.Audit,
    // Decisya.Modules.Admin) and exactly one PackageReference (the JwtBearer handler): EF Core
    // reaches Decisya.Api only transitively, through the modules, never as a direct package reference.
    [Fact]
    public void Decisya_Api_csproj_has_exactly_the_ServiceDefaults_Tenancy_Entitlements_Audit_and_Admin_ProjectReferences_and_only_the_JwtBearer_package()
    {
        var csprojPath = RepoPaths.Find(Path.Combine("src", "Decisya.Api", "Decisya.Api.csproj"));
        var content = File.ReadAllText(csprojPath);

        var projectReferenceCount = System.Text.RegularExpressions.Regex.Count(content, "<ProjectReference\\b");
        projectReferenceCount.Should().Be(5, "Decisya.Api should reference only Decisya.ServiceDefaults, Decisya.Modules.Tenancy, Decisya.Modules.Entitlements, Decisya.Modules.Audit and Decisya.Modules.Admin");
        content.Should().Contain("Decisya.ServiceDefaults.csproj");
        content.Should().Contain("Decisya.Modules.Tenancy.csproj");
        content.Should().Contain("Decisya.Modules.Entitlements.csproj");
        content.Should().Contain("Decisya.Modules.Audit.csproj");
        content.Should().Contain("Decisya.Modules.Admin.csproj");

        var packageReferenceIds = System.Text.RegularExpressions.Regex
            .Matches(content, "<PackageReference Include=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToList();

        packageReferenceIds.Should().BeEquivalentTo(["Microsoft.AspNetCore.Authentication.JwtBearer"]);
    }

    // G3 S-2 (issue #23, T-02; ADR-0012 point 4): Decisya.Api sits outside ArchitectureScope, and
    // TenantDbContext subclasses expose a public (options, ICurrentTenant) constructor, so any
    // type here that mints a tenant scope could open a module context for any tenant with no
    // rule noticing. Only CallerContextMiddleware (FromClaim, from the validated token's claim)
    // may reference these members. Same method-reference walk as CrossTenantQueryRule, whose
    // BypassList this set mirrors; #25's admin endpoint must not add a second type here.
    [Fact]
    public void Only_CallerContextMiddleware_mints_a_tenant_resolution_in_Decisya_Api()
    {
        var mintingMembers = new HashSet<string>(StringComparer.Ordinal) { "For", "FromClaim", "get_NoTenant" };
        var methodReferencingOpCodes = new HashSet<Mono.Cecil.Cil.OpCode>
        {
            Mono.Cecil.Cil.OpCodes.Call, Mono.Cecil.Cil.OpCodes.Callvirt, Mono.Cecil.Cil.OpCodes.Newobj,
            Mono.Cecil.Cil.OpCodes.Ldftn, Mono.Cecil.Cil.OpCodes.Ldvirtftn, Mono.Cecil.Cil.OpCodes.Ldtoken,
        };

        using var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(typeof(Program).Assembly.Location);

        var minters = assembly.MainModule.GetTypes()
            .SelectMany(type => type.Methods.Where(method => method.HasBody).Select(method => (type, method)))
            .Where(entry => entry.method.Body.Instructions.Any(instruction =>
                methodReferencingOpCodes.Contains(instruction.OpCode)
                && (instruction.Operand is Mono.Cecil.MethodReference reference
                    ? reference is Mono.Cecil.GenericInstanceMethod generic ? generic.ElementMethod : reference
                    : null) is { } target
                && target.DeclaringType.FullName == "Decisya.SharedKernel.Tenancy.TenantResolution"
                && mintingMembers.Contains(target.Name)))
            .Select(entry => OutermostType(entry.type).FullName)
            .Distinct()
            .Order()
            .ToList();

        minters.Should().Equal(["Decisya.Api.Authentication.CallerContextMiddleware"]);
    }

    private static Mono.Cecil.TypeDefinition OutermostType(Mono.Cecil.TypeDefinition type)
    {
        while (type.DeclaringType is not null)
        {
            type = type.DeclaringType;
        }

        return type;
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

    // #25 G2 (R-7), G3 G4-25-04: nothing under src/ may log or buffer a request body, and no
    // connection string or setting may ask Npgsql for the server's row detail (which would carry the
    // override reason in a check-constraint message).
    [Theory]
    [InlineData("AddHttpLogging")]
    [InlineData("UseHttpLogging")]
    [InlineData("AddW3CLogging")]
    [InlineData("UseW3CLogging")]
    [InlineData("EnableBuffering")]
    public void No_file_under_src_logs_or_buffers_a_request_body(string banned) =>
        FilesUnderSrcContaining(banned, "*.cs").Should().BeEmpty($"'{banned}' must never appear under src/ (G4-25-04)");

    [Theory]
    [InlineData("Include Error Detail")]
    [InlineData("IncludeErrorDetail(?!s)")] // not JwtBearer's unrelated IncludeErrorDetails (false since #20)
    public void No_file_under_src_enables_Npgsql_error_detail(string banned)
    {
        foreach (var pattern in new[] { "*.cs", "*.json", "*.csproj", "*.props", "*.targets" })
        {
            FilesUnderSrcContaining(banned, pattern, asRegex: true).Should().BeEmpty($"'{banned}' must never appear under src/ ({pattern}; G4-25-04)");
        }
    }

    // #25 G3 G4-25-05 (T-12): a GET must never become a DELETE or PUT at the API.
    [Fact]
    public void No_file_under_src_uses_UseHttpMethodOverride()
    {
        foreach (var pattern in new[] { "*.cs", "*.csproj" })
        {
            FilesUnderSrcContaining("UseHttpMethodOverride", pattern).Should().BeEmpty("method override is banned (G4-25-05)");
        }
    }

    private static List<string> FilesUnderSrcContaining(string needle, string filePattern, bool asRegex = false)
    {
        var srcRoot = RepoPaths.Find("src");
        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(srcRoot, filePattern, SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
                && !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal))
            .Where(path => asRegex
                ? System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(path), needle, System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                : File.ReadAllText(path).Contains(needle, StringComparison.Ordinal))
            .ToList();
    }

    public static IEnumerable<object[]> BoundaryCheckedAssemblies() =>
        BoundaryCheckedAssemblyNames.Select(name => new object[] { name });

    private static Assembly ResolveAssembly(string assemblyName) =>
        string.Equals(assemblyName, typeof(Program).Assembly.GetName().Name, StringComparison.Ordinal)
            ? typeof(Program).Assembly
            : Assembly.Load(assemblyName);
}
