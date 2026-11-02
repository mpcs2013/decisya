using System.Diagnostics;
using System.Text.RegularExpressions;
using Decisya.ArchitectureTests.Fixtures.TenancyViolations.PersonalDataLogs;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Green = Decisya.ArchitectureTests.Fixtures.TenancyCompliant.PersonalDataLogs;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #230, R1 to R4a and R6 to R10 (<see cref="PersonalDataLogRule"/>; register finding C-12; G1 Stories 1, 2 and 5;
/// G3 G4-230-01 to G4-230-04, S-02 and S-04). Red on a fixture per shape, green on the compliant fixtures, exact in its
/// suppressions, and green on today's <c>src</c> (<see cref="LogScope"/>). R5 is <see cref="PersonalDataCatalogueTests"/>.
/// </summary>
[Trait("Category", "Architecture")]
public partial class PersonalDataLogRuleTests(ITestOutputHelper output)
{
    private const string Sink = "ILogger.";

    private static readonly PersonalDataCatalogue Catalogue = PersonalDataCatalogue.Load();

    private static readonly Lazy<PersonalDataEvaluation> Red = new(() =>
        PersonalDataLogRule.Evaluate([typeof(UserIdOverCaller).Assembly.Location], Catalogue));

    private static readonly Lazy<PersonalDataEvaluation> GreenRun = new(() =>
        PersonalDataLogRule.Evaluate([typeof(Green.OwningObject).Assembly.Location], Catalogue));

    private static readonly Lazy<PersonalDataEvaluation> Src = new(() =>
        PersonalDataLogRule.Evaluate(LogScope.AssemblyPaths, Catalogue));

    [GeneratedRegex(@"[A-Za-z]:[\\/]|Users", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DriveOrUser();

    private static List<PersonalDataFinding> At(PersonalDataEvaluation evaluation, Type fixture, string method) =>
        [.. evaluation.Findings.Where(f => string.Equals(f.Site, $"{fixture.FullName}::{method}", StringComparison.Ordinal))];

    private static string SiteOf(Type fixture, string method) => $"{fixture.FullName}::{method}";

    // ---------------------------------------------------------------- R1: one fixture per category and shape

    [Fact]
    public void R1_F1_the_caller_user_id_under_an_innocent_placeholder_is_reported_with_file_line_and_category()
    {
        var findings = At(Red.Value, typeof(UserIdOverCaller), "Run");

        findings.Should().ContainSingle();
        var finding = findings[0];
        finding.CategoryId.Should().Be("user-identifier");
        finding.CategoryName.Should().Be("User identifier");
        finding.File.Should().EndWith("Fixtures/TenancyViolations/PersonalDataLogs/PersonalDataLogViolations.cs");
        finding.Line.Should().BePositive();
        finding.Sink.Should().StartWith(Sink);
    }

    [Theory]
    [InlineData("LogUserId", "user-identifier")]
    [InlineData("LogEmail", "contact")]
    [InlineData("LogDisplayName", "name")]
    [InlineData("LogIban", "account-number")]
    [InlineData("LogDescription", "free-text")]
    [InlineData("LogPrompt", "ai-text")]
    [InlineData("LogClientSecret", "secret-material")]
    public void R1_F2_to_F8_each_of_the_seven_categories_is_reported_under_a_plain_Value_placeholder(string method, string category)
    {
        At(Red.Value, typeof(EachCategory), method).Should().Contain(f => f.CategoryId == category);
    }

    [Fact]
    public void R1_every_category_of_the_catalogue_has_a_red_fixture()
    {
        foreach (var category in Catalogue.Categories)
        {
            Red.Value.Findings.Should().Contain(
                f => f.CategoryId == category.Id && f.Site.StartsWith(typeof(EachCategory).FullName!, StringComparison.Ordinal),
                $"category '{category.Id}' needs a red fixture");
        }
    }

    [Fact]
    public void R1_F9_a_LoggerMessage_parameter_fed_from_a_description_is_reported_at_the_call_site()
    {
        var findings = At(Red.Value, typeof(LoggerMessageFed), "Run");

        findings.Should().Contain(f => f.CategoryId == "free-text" && f.Sink.Contains("[LoggerMessage]", StringComparison.Ordinal));
    }

    [Fact]
    public void R1_F10_an_interpolated_template_is_reported()
    {
        At(Red.Value, typeof(InterpolatedTemplate), "Run").Should().Contain(f => f.CategoryId == "user-identifier");
    }

    [Fact]
    public void R1_F11_the_15_case_LogInformation_Paid_Payee_with_a_description_is_reported()
    {
        At(Red.Value, typeof(PaidPayee), "Run").Should().Contain(f => f.CategoryId == "free-text");
    }

    // ---------------------------------------------------------------- R2: one fixture per lowering

    [Theory]
    [InlineData(typeof(LocalHop), "Run")]
    [InlineData(typeof(AwaitBetween), "Run")]
    [InlineData(typeof(ClosureCapture), "Run")]
    [InlineData(typeof(Conditionals), "Ternary")]
    [InlineData(typeof(Conditionals), "Coalesce")]
    [InlineData(typeof(ConcatAndBuilder), "Concat")]
    [InlineData(typeof(ConcatAndBuilder), "Builder")]
    [InlineData(typeof(ScopeWithMap), "Run")]
    [InlineData(typeof(ExceptionMessage), "Run")]
    [InlineData(typeof(HelperForwarding), "Run")]
    [InlineData(typeof(ReturnsValue), "Run")]
    [InlineData(typeof(FieldSetElsewhere), "Emit")]
    [InlineData(typeof(DefineInvoke), "Run")]
    [InlineData(typeof(ClaimSub), "Run")]
    [InlineData(typeof(ClaimsEnumerated), "Run")]
    public void R2_each_lowering_of_a_personal_value_into_a_sink_is_reported(Type fixture, string method)
    {
        At(Red.Value, fixture, method).Should().NotBeEmpty($"{fixture.Name}.{method} passes personal data to a sink");
    }

    [Fact]
    public void R2_a_helper_that_forwards_its_parameter_is_reported_at_the_call_site_and_not_inside_the_helper()
    {
        At(Red.Value, typeof(HelperForwarding), "Run").Should().NotBeEmpty();
        At(Red.Value, typeof(HelperForwarding), "Forward").Should().BeEmpty("a parameter is not personal data by itself");
    }

    // ---------------------------------------------------------------- R3: [Sensitive] semantics

    [Fact]
    public void R3_a_scalar_read_from_a_Sensitive_member_is_reported_and_labelled_by_its_category_or_Marked_Sensitive()
    {
        At(Red.Value, typeof(ScalarFromSensitive), "NoCategory").Should()
            .Contain(f => f.CategoryId == "marked-sensitive" && f.CategoryName == "Marked [Sensitive]");
        At(Red.Value, typeof(ScalarFromSensitive), "InCategory").Should().Contain(f => f.CategoryId == "user-identifier");
    }

    [Fact]
    public void R3_an_object_passed_whole_whose_personal_member_lacks_Sensitive_is_reported()
    {
        At(Red.Value, typeof(WholeWithUnmarkedMember), "Run").Should().Contain(f => f.CategoryId == "contact");
    }

    [Fact]
    public void R3_a_record_with_a_Sensitive_member_and_a_synthesized_ToString_is_reported_when_interpolated()
    {
        At(Red.Value, typeof(RecordInterpolated), "Run").Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(typeof(Green.OwningObject))]
    [InlineData(typeof(Green.SensitiveTypeWhole))]
    [InlineData(typeof(Green.ListOfOwningObjects))]
    public void R3_green_the_owning_object_a_Sensitive_type_and_a_list_of_owning_objects_pass(Type fixture)
    {
        GreenRun.Value.Findings.Where(f => f.Site.StartsWith(fixture.FullName!, StringComparison.Ordinal))
            .Should().BeEmpty(string.Join("; ", GreenRun.Value.Findings));
    }

    // ---------------------------------------------------------------- R4: green with zero findings

    [Fact]
    public void R4_the_compliant_fixtures_have_zero_findings_except_the_pass_through_masker_which_needs_its_catalogue_entry()
    {
        var findings = GreenRun.Value.Findings.Where(f => !f.Site.StartsWith(typeof(Green.MaskerOutput).FullName!, StringComparison.Ordinal)).ToList();

        findings.Should().BeEmpty(string.Join("; ", findings));
    }

    [Fact]
    public void R4_masker_output_is_clean_only_when_the_masker_is_listed_so_the_green_is_not_vacuous()
    {
        var site = typeof(Green.MaskerOutput).FullName!;
        GreenRun.Value.Findings.Should().Contain(f => f.Site.StartsWith(site, StringComparison.Ordinal), "without the masker the pass-through is a leak");

        var listed = Catalogue with { Maskers = [.. Catalogue.Maskers, typeof(Green.FixtureMasker).FullName + "::Mask"] };
        var result = PersonalDataLogRule.Evaluate([typeof(Green.OwningObject).Assembly.Location], listed);

        result.Findings.Should().BeEmpty(string.Join("; ", result.Findings));
    }

    [Theory]
    [InlineData("SyncPath")]
    [InlineData("AsyncPath")]
    public void R4_logging_only_FailureReason_of_a_record_built_by_Valid_and_Failed_is_clean_on_the_sync_and_async_path(string method)
    {
        var findings = At(GreenRun.Value, typeof(Green.LogoutRejectionLog), method);

        findings.Should().BeEmpty(string.Join("; ", findings));
    }

    [Theory]
    [InlineData("SyncPath")]
    [InlineData("AsyncPath")]
    public void R4a_logging_Subject_of_the_same_record_shape_is_reported_as_a_User_identifier_from_JsonWebToken_TryGetPayloadValue(string method)
    {
        var findings = At(Red.Value, typeof(LeakyLogoutLog), method);

        findings.Should().NotBeEmpty($"{method} logs the subject claim");
        findings.Should().Contain(f => f.CategoryId == "user-identifier" && f.CategoryName == "User identifier"
            && f.Source.Contains("JsonWebToken::TryGetPayloadValue", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- R4a: the G3 MUSTs

    [Theory]
    [InlineData("LoggedWhole")]
    [InlineData("Interpolated")]
    [InlineData("Initializer")]
    [InlineData("ValueObject")]
    [InlineData("GuidMember")]
    public void R4a_G4_230_01_taint_survives_a_Decisya_object_and_non_string_personal_members_are_caught(string method)
    {
        At(Red.Value, typeof(WrappedInDecisyaObject), method).Should().NotBeEmpty($"{method} must be reported");
    }

    [Theory]
    [InlineData("JwtPayload", "user-identifier")]
    [InlineData("RequestCookie", "secret-material")]
    [InlineData("StoredToken", "secret-material")]
    [InlineData("HttpContextToken", "secret-material")]
    public void R4a_G4_230_02_the_framework_readers_of_tokens_cookies_and_jwt_payloads_are_sources(string method, string category)
    {
        At(Red.Value, typeof(FrameworkReaders), method).Should().Contain(f => f.CategoryId == category);
    }

    [Theory]
    [InlineData("ConsoleWrite", "Console.")]
    [InlineData("ConsoleErrorWrite", "Console.")]
    [InlineData("DebugWrite", "Debug.")]
    [InlineData("TraceWrite", "Trace.")]
    public void R4a_G4_230_03_Console_Debug_and_Trace_writes_are_sinks(string method, string sink)
    {
        At(Red.Value, typeof(UnstructuredWrites), method).Should().Contain(f => f.Sink.StartsWith(sink, StringComparison.Ordinal));
    }

    [Fact]
    public void R4a_G4_230_04_a_hand_written_GeneratedCode_method_is_examined_as_usual()
    {
        At(Red.Value, typeof(HandWrittenGenerated), "Run").Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("GeneratedCodeType")]
    [InlineData("CompilerGeneratedType")]
    public void R4a_G6_230_01_a_hand_written_type_marked_generated_is_checked_for_unmarked_personal_members(string method)
    {
        At(Red.Value, typeof(HandWrittenGeneratedTypes), method).Should().Contain(f => f.CategoryId == "contact");
    }

    [Fact]
    public void R4a_the_generated_body_of_a_LoggerMessage_method_is_skipped_but_its_call_site_is_not()
    {
        Red.Value.Findings.Should().NotContain(f => f.Site.StartsWith(typeof(LoggerMessageFed).FullName + "::Paid", StringComparison.Ordinal));
        Red.Value.Stats.LoggerMessageCallSites.Should().Contain(s => s.Contains("LoggerMessageFed::Paid", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- R6: suppression

    [Fact]
    public void R6_a_valid_attribute_with_a_matching_catalogue_entry_is_a_counted_suppression_and_the_finding_goes()
    {
        var entry = new SuppressionEntry(SiteOf(typeof(ReviewedFinding), "Run"), "free-text", ReviewedFinding.Reason);
        var result = PersonalDataLogRule.Evaluate([typeof(ReviewedFinding).Assembly.Location], Catalogue with { Suppressions = [entry] });

        result.Suppressions.Should().Contain(new PersonalDataSuppression(entry.Site, entry.Category, entry.Justification));
        At(result, typeof(ReviewedFinding), "Run").Should().BeEmpty();
        result.Problems.Should().NotContain(p => p.StartsWith(entry.Site, StringComparison.Ordinal));
    }

    [Fact]
    public void R6_an_attribute_with_no_matching_catalogue_entry_fails_and_an_entry_with_no_matching_use_fails_as_stale()
    {
        Red.Value.Problems.Should().Contain(p => p.StartsWith(SiteOf(typeof(UnpinnedReview), "Run"), StringComparison.Ordinal)
            && p.Contains("no matching entry", StringComparison.Ordinal));

        var stale = new SuppressionEntry("Decisya.Nope.Gone::Run", "free-text", "Fixture: an entry whose use is gone");
        var result = PersonalDataLogRule.Evaluate([typeof(ReviewedFinding).Assembly.Location], Catalogue with { Suppressions = [stale] });

        result.Problems.Should().Contain(p => p.Contains("Decisya.Nope.Gone::Run", StringComparison.Ordinal) && p.Contains("stale", StringComparison.Ordinal));
    }

    [Fact]
    public void R6_a_changed_justification_does_not_match_its_pin()
    {
        var entry = new SuppressionEntry(SiteOf(typeof(ReviewedFinding), "Run"), "free-text", ReviewedFinding.Reason + " (changed)");
        var result = PersonalDataLogRule.Evaluate([typeof(ReviewedFinding).Assembly.Location], Catalogue with { Suppressions = [entry] });

        result.Problems.Should().Contain(p => p.StartsWith(entry.Site, StringComparison.Ordinal) && p.Contains("no matching entry", StringComparison.Ordinal));
        result.Problems.Should().Contain(p => p.Contains("stale", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(typeof(BlankReview), "blank")]
    [InlineData(typeof(ShortReview), "under 20")]
    [InlineData(typeof(UnknownCategoryReview), "unknown category")]
    public void R6_a_blank_short_or_unknown_category_use_fails_and_does_not_suppress(Type fixture, string phrase)
    {
        Red.Value.Problems.Should().Contain(p => p.StartsWith(SiteOf(fixture, "Run"), StringComparison.Ordinal) && p.Contains(phrase, StringComparison.Ordinal));
        At(Red.Value, fixture, "Run").Should().NotBeEmpty("an invalid use suppresses nothing");
    }

    [Fact]
    public void R6_a_suppression_naming_secret_material_fails_as_an_attribute_and_as_an_entry_and_hides_nothing()
    {
        Red.Value.Problems.Should().Contain(p => p.StartsWith(SiteOf(typeof(SecretReview), "Run"), StringComparison.Ordinal)
            && p.Contains("never be suppressed", StringComparison.Ordinal));
        At(Red.Value, typeof(SecretReview), "Run").Should().Contain(f => f.CategoryId == "secret-material");

        var entry = new SuppressionEntry(SiteOf(typeof(SecretReview), "Run"), "secret-material", "Fixture: a long enough justification text");
        var result = PersonalDataLogRule.Evaluate([typeof(SecretReview).Assembly.Location], Catalogue with { Suppressions = [entry] });
        result.Problems.Should().Contain(p => p.Contains("Catalogue suppression", StringComparison.Ordinal) && p.Contains("never be suppressed", StringComparison.Ordinal));
    }

    [Fact]
    public void R6_a_suppression_for_category_A_does_not_hide_category_B()
    {
        var entry = new SuppressionEntry(SiteOf(typeof(CategoryOnlyReview), "Run"), "free-text", "Fixture: the description is a fixed bucket label");
        var result = PersonalDataLogRule.Evaluate([typeof(CategoryOnlyReview).Assembly.Location], Catalogue with { Suppressions = [entry] });

        var findings = At(result, typeof(CategoryOnlyReview), "Run");
        findings.Should().Contain(f => f.CategoryId == "contact");
        findings.Should().NotContain(f => f.CategoryId == "free-text");
    }

    [Fact]
    public void R6_seven_attributes_on_one_method_are_seven_pinned_entries()
    {
        var site = SiteOf(typeof(SevenAttributes), "Run");
        var pins = new[]
        {
            new SuppressionEntry(site, "user-identifier", SevenAttributes.Reason + " one"),
            new SuppressionEntry(site, "contact", SevenAttributes.Reason + " two"),
            new SuppressionEntry(site, "name", SevenAttributes.Reason + " three"),
            new SuppressionEntry(site, "account-number", SevenAttributes.Reason + " four"),
            new SuppressionEntry(site, "free-text", SevenAttributes.Reason + " five"),
            new SuppressionEntry(site, "ai-text", SevenAttributes.Reason + " six"),
            new SuppressionEntry(site, "free-text", SevenAttributes.Reason + " seven"),
        };

        var all = PersonalDataLogRule.Evaluate([typeof(SevenAttributes).Assembly.Location], Catalogue with { Suppressions = pins });
        all.Suppressions.Where(s => s.Site == site).Should().HaveCount(7);
        At(all, typeof(SevenAttributes), "Run").Should().BeEmpty();
        all.Problems.Should().NotContain(p => p.StartsWith(site, StringComparison.Ordinal));

        var missing = PersonalDataLogRule.Evaluate([typeof(SevenAttributes).Assembly.Location], Catalogue with { Suppressions = [.. pins.Take(6)] });
        missing.Problems.Should().Contain(p => p.StartsWith(site, StringComparison.Ordinal) && p.Contains("no matching entry", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- R7: today's src

    [Fact]
    public void R7_today_src_has_zero_findings_and_the_suppression_list_equals_the_catalogue_which_is_empty()
    {
        var stopwatch = Stopwatch.StartNew();
        var result = Src.Value;
        stopwatch.Stop();

        output.WriteLine($"PersonalDataLogRule over src: {result.Stats} (first access {stopwatch.ElapsedMilliseconds} ms)");
        output.WriteLine($"suppressions: {result.Suppressions.Count}");
        foreach (var suppression in result.Suppressions)
        {
            output.WriteLine($"  {suppression.Site} [{suppression.Category}] {suppression.Justification}");
        }

        LogScope.Problems.Should().BeEmpty(string.Join("; ", LogScope.Problems));
        result.Findings.Should().BeEmpty(string.Join(Environment.NewLine, result.Findings));
        result.Problems.Should().BeEmpty(string.Join("; ", result.Problems));
        result.Suppressions.Select(s => new SuppressionEntry(s.Site, s.Category, s.Justification))
            .Should().BeEquivalentTo(Catalogue.Suppressions);
        Catalogue.Suppressions.Should().BeEmpty("G1 Story 2: existing findings are fixed, not suppressed");
    }

    [Fact]
    public void R7_the_green_run_is_not_vacuous_every_LoggerMessage_call_site_and_the_known_sources_were_examined()
    {
        var stats = Src.Value.Stats;

        stats.LoggerMessageCallSitesInIl.Should().NotBeEmpty();
        stats.LoggerMessageCallSites.Should().BeEquivalentTo(stats.LoggerMessageCallSitesInIl, "every LoggerMessage call site in src was examined");
        stats.LoggerMessageCallSites.Should().Contain(s =>
            s.Contains("PlatformAdminAuthorizationHandler", StringComparison.Ordinal) && s.Contains("AdminLog::AccessRefused", StringComparison.Ordinal));
        stats.SourcesSeen.Should().Contain(s =>
            s.Contains("TenantMembershipGate", StringComparison.Ordinal) && s.Contains("ICurrentCaller::UserId", StringComparison.Ordinal));
        stats.SourcesSeen.Should().Contain(s =>
            s.Contains("AccessTokenProvider", StringComparison.Ordinal) && s.Contains("GetTokenValue", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- R8: failure text and output hygiene

    [Fact]
    public void R8_the_failure_text_holds_the_file_the_line_the_category_name_and_all_three_fixes()
    {
        var finding = At(Red.Value, typeof(PaidPayee), "Run").Should().ContainSingle().Subject;

        var text = finding.ToString();
        text.Should().StartWith("tests/Decisya.ArchitectureTests/Fixtures/TenancyViolations/PersonalDataLogs/PersonalDataLogViolations.cs:");
        text.Should().Contain($":{finding.Line}:").And.Contain("Free text");
        text.Should().Contain("pass the owning object whose member carries [Sensitive]");
        text.Should().Contain("rely on the hashed user_id from ILogEnrichmentContext");
        text.Should().Contain("drop the value");
        finding.Line.Should().BePositive();
    }

    [Fact]
    public void R8_S_02_the_output_has_no_drive_letter_no_users_segment_and_no_il_string_literal()
    {
        var canary = At(Red.Value, typeof(CanaryLiteral), "Run");
        canary.Should().NotBeEmpty();

        var everything = string.Join(Environment.NewLine, Red.Value.Findings.Select(f => f.ToString()))
            + Environment.NewLine + Red.Value.Stats
            + Environment.NewLine + string.Join(Environment.NewLine, Src.Value.Stats.SourcesSeen)
            + Environment.NewLine + string.Join(Environment.NewLine, Red.Value.Problems);

        everything.Should().NotContain("canary-template-literal-9c1e");
        DriveOrUser().IsMatch(everything).Should().BeFalse("paths are repo-relative and no user name is printed");
    }

    [Theory]
    [InlineData(@"C:\Users\someone\src\x.cs", "x.cs")]
    [InlineData("/_/src/Decisya.Api/X.cs", "src/Decisya.Api/X.cs")]
    [InlineData("/home/someone/src/x.cs", "x.cs")]
    public void R8_S_02_a_path_outside_the_repository_prints_the_file_name_only_and_a_ci_prefix_is_stripped(string url, string expected)
    {
        PersonalDataWorld.RelativePath(url).Should().Be(expected);
    }

    // ---------------------------------------------------------------- R9: LogScope

    [Fact]
    public void R9_LogScope_is_every_src_project_with_no_exclusion_and_the_AppHost_and_Migrator_are_read_from_disk()
    {
        var all = Directory.EnumerateFiles(RepoPaths.Find("src"), "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToList();

        LogScope.ProjectNames.Should().Equal(all);
        LogScope.DiskOnly.Should().Equal("Decisya.AppHost", "Decisya.Infrastructure.Migrator");
        LogScope.Problems.Should().BeEmpty(string.Join("; ", LogScope.Problems));
        LogScope.AssemblyPaths.Should().HaveCount(all.Count);
        foreach (var project in LogScope.DiskOnly)
        {
            LogScope.AssemblyPaths.Should().Contain(path => string.Equals(Path.GetFileName(path), project + ".dll", StringComparison.Ordinal));
            AppDomain.CurrentDomain.GetAssemblies().Should().NotContain(a => string.Equals(a.GetName().Name, project, StringComparison.Ordinal), "it is read with Cecil, never loaded");
        }
    }

    [Fact]
    public void R9_a_missing_or_stale_build_output_fails_and_names_the_project()
    {
        var root = Path.Combine(Path.GetTempPath(), "decisya-logscope-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = Path.Combine(root, "Some.Project");
            Directory.CreateDirectory(project);
            var source = Path.Combine(project, "Source.cs");
            File.WriteAllText(source, "// source");
            var dll = Path.Combine(root, "Some.Project.dll");

            LogScope.ProblemsFor("Some.Project", Path.Combine(root, "missing.dll"), project)
                .Should().ContainSingle().Which.Should().Contain("Some.Project").And.Contain("build the solution first");

            File.WriteAllText(dll, "dll");
            LogScope.ProblemsFor("Some.Project", dll, project)
                .Should().ContainSingle().Which.Should().Contain("PDB").And.Contain("Some.Project");

            File.WriteAllText(Path.ChangeExtension(dll, ".pdb"), "pdb");
            File.SetLastWriteTimeUtc(dll, File.GetLastWriteTimeUtc(source).AddMinutes(-5));
            LogScope.ProblemsFor("Some.Project", dll, project)
                .Should().ContainSingle().Which.Should().Contain("stale").And.Contain("Some.Project");

            File.SetLastWriteTimeUtc(dll, File.GetLastWriteTimeUtc(source).AddMinutes(5));
            LogScope.ProblemsFor("Some.Project", dll, project).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---------------------------------------------------------------- R10: S-04, the enrichment context only ever hashes the user id

    [Fact]
    public void R10_S_04_LogEnrichmentContext_is_the_only_ILogEnrichmentContext_and_Begin_passes_userId_only_to_the_hasher()
    {
        const string interfaceName = "Decisya.ServiceDefaults.Logging.ILogEnrichmentContext";
        var implementations = new List<TypeDefinition>();
        var modules = new List<ModuleDefinition>();
        try
        {
            foreach (var path in LogScope.AssemblyPaths)
            {
                var module = ModuleDefinition.ReadModule(path);
                modules.Add(module);
                implementations.AddRange(module.GetTypes().Where(t => t.Interfaces.Any(i => i.InterfaceType.FullName == interfaceName)));
            }

            implementations.Should().ContainSingle().Which.FullName.Should().Be("Decisya.ServiceDefaults.Logging.LogEnrichmentContext");

            var begin = implementations[0].Methods.Single(m => m.Name == "Begin");
            var userId = begin.Parameters.Single(p => p.Name == "userId");
            var instructions = begin.Body.Instructions;
            var uses = 0;
            for (var i = 0; i < instructions.Count; i++)
            {
                var instruction = instructions[i];
                var loadsUserId = instruction.Operand is ParameterDefinition parameter && parameter.Name == userId.Name
                    || (begin.HasThis && instruction.OpCode.Code == Code.Ldarg_2);
                if (!loadsUserId)
                {
                    instruction.OpCode.Code.Should().NotBe(Code.Starg_S).And.NotBe(Code.Starg);
                    continue;
                }

                instruction.OpCode.Code.Should().NotBe(Code.Ldarga_S).And.NotBe(Code.Ldarga).And.NotBe(Code.Starg_S).And.NotBe(Code.Starg);
                var next = instructions[i + 1];
                next.Operand.Should().BeAssignableTo<MethodReference>();
                var target = (MethodReference)next.Operand;
                target.DeclaringType.FullName.Should().Be("Decisya.ServiceDefaults.Logging.UserIdHasher");
                target.Name.Should().Be("Hash");
                uses++;
            }

            uses.Should().BeGreaterThan(0, "Begin does read userId, to hash it");
        }
        finally
        {
            foreach (var module in modules)
            {
                module.Dispose();
            }
        }
    }
}
