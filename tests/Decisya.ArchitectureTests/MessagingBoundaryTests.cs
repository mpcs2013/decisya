using Decisya.ArchitectureTests.Fixtures.TenancyCompliant.MessageHandlers;
using Decisya.ArchitectureTests.Fixtures.TenancyViolations.MessageHandlers;
using Decisya.ArchitectureTests.Fixtures.TenancyViolations.WolverineTenantSetters;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #231, R5 (<see cref="MessageHandlerListRule"/>: module handler lists; no module references Wolverine),
/// G3 G4-231-03 (no <c>[AllowCrossTenant]</c> handler on a message path), G1 Story 6 scenario 2 (a handler
/// without a two-tenant isolation test is caught) and R15 (<see cref="RuntimeCompilerRule"/>: the runtime
/// compiler stays out of <c>src</c>, G3 S-11). Each is red on a fixture or synthetic input and green on
/// today's <c>src</c>.
/// </summary>
[Trait("Category", "Architecture")]
public class MessagingBoundaryTests
{
    private static readonly IReadOnlyCollection<IsolationTestFile> NoIsolationTests = [];

    private static readonly IsolationTestFile TestedLedgerIsolationFile = new(
        "LedgerIsolationTests.cs",
        "[Trait(\"Category\", \"Integration\")] public class LedgerIsolationTests { object H => typeof(TestedLedgerHandler); }");

    private static List<System.Reflection.Assembly> ModuleAssemblies() =>
    [
        .. ArchitectureScope.Assemblies.Where(a => (a.GetName().Name ?? string.Empty).StartsWith("Decisya.Modules.", StringComparison.Ordinal)),
    ];

    // ---- R5: no module (or Contracts) assembly references Wolverine ----

    [Fact]
    public void R5_no_module_and_no_Contracts_assembly_references_the_Wolverine_assembly()
    {
        var modules = ModuleAssemblies();
        modules.Should().NotBeEmpty("the scope must not be vacuous");

        MessageHandlerListRule.WolverineReferences(modules).Should().BeEmpty();
        MessageHandlerListRule.WolverineReferences(ContractsScope.Assemblies).Should().BeEmpty();
    }

    [Fact]
    public void R5_the_reference_check_sees_an_assembly_that_does_reference_Wolverine()
    {
        MessageHandlerListRule.WolverineReferences([typeof(SetsEnvelopeTenant).Assembly])
            .Should().Equal(typeof(SetsEnvelopeTenant).Assembly.GetName().Name!);
    }

    // ---- R5: a listed handler is declared in the listing module's own assembly (G1 Story 3 scenario 3) ----

    [Fact]
    public void R5_fails_on_a_module_that_lists_another_assemblys_handler_and_names_the_module()
    {
        var failures = MessageHandlerListRule.Evaluate([typeof(ForeignHandlerModule).Assembly], NoIsolationTests);

        failures.Should().Contain(f =>
            f.StartsWith(typeof(ForeignHandlerModule).FullName!, StringComparison.Ordinal)
            && f.Contains("not in the module's own assembly", StringComparison.Ordinal));
    }

    [Fact]
    public void R5_passes_a_module_that_lists_its_own_handler_with_an_isolation_test()
    {
        var failures = MessageHandlerListRule.Evaluate([typeof(CompliantHandlerModule).Assembly], [TestedLedgerIsolationFile]);

        failures.Should().BeEmpty(string.Join("; ", failures));
        CompliantHandlerModule.MessageHandlerTypes.Should().Equal(typeof(TestedLedgerHandler));
    }

    // ---- G4-231-03: no [AllowCrossTenant] handler on a message path ----

    [Fact]
    public void G4_231_03_fails_on_a_module_that_lists_an_AllowCrossTenant_handler_and_the_allow_list_is_empty()
    {
        var failures = MessageHandlerListRule.Evaluate([typeof(CrossTenantHandlerModule).Assembly], NoIsolationTests);

        failures.Should().Contain(f =>
            f.StartsWith(typeof(CrossTenantHandlerModule).FullName!, StringComparison.Ordinal)
            && f.Contains("[AllowCrossTenant]", StringComparison.Ordinal));
        MessageHandlerListRule.CrossTenantHandlerAllowList.Should().BeEmpty("the first cross-tenant sender's G2 and G3 add one exact entry");
    }

    // ---- Story 6 scenario 2: a handler without a two-tenant isolation test is caught ----

    [Fact]
    public void Story6_a_new_message_handler_without_an_isolation_test_is_reported()
    {
        var failures = MessageHandlerListRule.Evaluate([typeof(UntestedHandlerModule).Assembly], NoIsolationTests);

        failures.Should().Contain(f =>
            f.StartsWith(typeof(UntestedHandlerModule).FullName!, StringComparison.Ordinal)
            && f.Contains(nameof(UntestedLedgerHandler), StringComparison.Ordinal)
            && f.Contains("no two-tenant isolation test", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("LedgerTests.cs", "[Trait(\"Category\", \"Integration\")] object H => typeof(UntestedLedgerHandler);")]
    [InlineData("LedgerIsolationTests.cs", "[Trait(\"Category\", \"Unit\")] object H => typeof(UntestedLedgerHandler);")]
    [InlineData("LedgerIsolationTests.cs", "[Trait(\"Category\", \"Integration\")] object H => typeof(UntestedLedgerHandlerX);")]
    public void Story6_a_file_that_is_not_an_isolation_test_for_that_handler_does_not_count(string fileName, string text)
    {
        var failures = MessageHandlerListRule.Evaluate([typeof(UntestedHandlerModule).Assembly], [new IsolationTestFile(fileName, text)]);

        failures.Should().Contain(f =>
            f.StartsWith(typeof(UntestedHandlerModule).FullName!, StringComparison.Ordinal)
            && f.Contains("no two-tenant isolation test", StringComparison.Ordinal));
    }

    [Fact]
    public void Story6_a_matching_isolation_test_clears_the_report_for_that_handler()
    {
        var file = new IsolationTestFile(
            "LedgerIsolationTests.cs",
            "[Trait(\"Category\", \"Integration\")] public class LedgerIsolationTests { object H => typeof(UntestedLedgerHandler); }");

        var failures = MessageHandlerListRule.Evaluate([typeof(UntestedHandlerModule).Assembly], [file]);

        failures.Should().NotContain(f =>
            f.StartsWith(typeof(UntestedHandlerModule).FullName!, StringComparison.Ordinal)
            && f.Contains("no two-tenant isolation test", StringComparison.Ordinal));
    }

    [Fact]
    public void R5_is_green_on_today_src_where_no_module_lists_a_handler_yet()
    {
        var modules = ModuleAssemblies();
        modules.Should().NotBeEmpty();

        var failures = MessageHandlerListRule.Evaluate(modules, MessageHandlerListRule.FromRepository());

        failures.Should().BeEmpty(string.Join("; ", failures));
    }

    // ---- R15: the runtime compiler stays out of src (ADR-0024 amendment 1 item 5; G3 S-11) ----

    [Theory]
    [InlineData("<Project><ItemGroup><PackageReference Include=\"WolverineFx.RuntimeCompilation\" /></ItemGroup></Project>")]
    [InlineData("<Project><ItemGroup><PackageReference Update=\"WolverineFx.RuntimeCompilation\" Version=\"6.39.1\" /></ItemGroup></Project>")]
    [InlineData("<Project><ItemGroup><PackageReference Include=\"JasperFx.RuntimeCompiler\" /></ItemGroup></Project>")]
    public void R15_fails_on_a_project_file_that_references_the_runtime_compiler(string projectText)
    {
        var violations = RuntimeCompilerRule.ProjectFileViolations([new SourceFile("src/Fixture/Fixture.csproj", projectText)]);

        violations.Should().Equal("src/Fixture/Fixture.csproj");
    }

    [Fact]
    public void R15_passes_a_project_file_that_references_only_WolverineFx()
    {
        var violations = RuntimeCompilerRule.ProjectFileViolations(
            [new SourceFile("src/Fixture/Fixture.csproj", "<Project><ItemGroup><PackageReference Include=\"WolverineFx\" /></ItemGroup></Project>")]);

        violations.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{\"version\":2,\"dependencies\":{\"net10.0\":{\"WolverineFx.RuntimeCompilation\":{\"type\":\"Direct\"}}}}")]
    [InlineData("{\"version\":2,\"dependencies\":{\"net10.0\":{\"SomePackage\":{\"type\":\"Direct\"},\"JasperFx.RuntimeCompiler\":{\"type\":\"Transitive\"}}}}")]
    public void R15_fails_on_a_lock_file_that_lists_the_runtime_compiler_even_transitively(string lockText)
    {
        var violations = RuntimeCompilerRule.LockFileViolations([new SourceFile("src/Fixture/packages.lock.json", lockText)]);

        violations.Should().Equal("src/Fixture/packages.lock.json");
    }

    [Fact]
    public void R15_passes_a_lock_file_that_lists_WolverineFx_but_not_the_runtime_compiler()
    {
        var violations = RuntimeCompilerRule.LockFileViolations(
            [new SourceFile("src/Fixture/packages.lock.json", "{\"version\":2,\"dependencies\":{\"net10.0\":{\"WolverineFx\":{\"type\":\"Direct\"}}}}")]);

        violations.Should().BeEmpty();
    }

    [Fact]
    public void R15_fails_on_a_reference_to_either_runtime_compiler_assembly_and_ignores_Wolverine_itself()
    {
        RuntimeCompilerRule.BannedReferenceNames(["Wolverine", "Wolverine.RuntimeCompilation", null, "JasperFx", "JasperFx.RuntimeCompiler"])
            .Should().Equal("Wolverine.RuntimeCompilation", "JasperFx.RuntimeCompiler");
    }

    [Fact]
    public void R15_is_green_on_the_real_src_tree_and_on_every_scope_assembly()
    {
        var projects = RuntimeCompilerRule.ProjectFilesUnderSrc();
        var locks = RuntimeCompilerRule.LockFilesUnderSrc();

        // Not vacuous: the scan reads the Api's project file and lock file, which do carry WolverineFx.
        projects.Should().Contain(p => p.RelativePath.EndsWith("Decisya.Api.csproj", StringComparison.Ordinal) && p.Text.Contains("\"WolverineFx\"", StringComparison.Ordinal));
        locks.Should().Contain(l => l.RelativePath.Contains("Decisya.Api", StringComparison.Ordinal) && l.Text.Contains("\"WolverineFx\"", StringComparison.Ordinal));

        RuntimeCompilerRule.ProjectFileViolations(projects).Should().BeEmpty();
        RuntimeCompilerRule.LockFileViolations(locks).Should().BeEmpty();
        RuntimeCompilerRule.AssemblyViolations(KeyScope.Assemblies).Should().BeEmpty();
    }
}
