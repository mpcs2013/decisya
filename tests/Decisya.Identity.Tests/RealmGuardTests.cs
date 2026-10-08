using System.Text.Json;
using System.Text.Json.Serialization;

namespace Decisya.Identity.Tests;

/// <summary>
/// G4-77 (issue #77, G4 part 1 of 3): the scoped realm-file guard. Replaces
/// <c>RealmExportFileTests.The_realm_file_name_is_referenced_only_from_the_AppHost_tests_and_docs</c>
/// (G4-17-12), which used an unanchored absolute-path prefix match (T77-11) and a
/// directory-wide `.claude/` allowance that would have reopened T77-01. No
/// <c>[Trait("Category", "Integration")]</c>: everything here reads files already on disk, no
/// Docker. See docs/security/threat-models/realm-guard-scope.md and
/// docs/security/reviews/77.md (G6-77-01 through 11) for the guard-side fixes below.
/// </summary>
public class RealmGuardTests
{
    /// <summary>
    /// The realm-file needle, spelled out once so the case-table assertions below (G6-77-06)
    /// don't need a production-code hook just to check "does this row's content name the file".
    /// This file lives under `tests/`, itself unconditionally exempt, so writing the literal
    /// name here does not trip the guard.
    /// </summary>
    private const string Needle = "decisya-realm.json";

    /// <summary>
    /// The container import directory is the guard's second needle (G4-77-13). The #121 rule is
    /// decisive only for rows that name it, so the "sole reason" meta-test accepts either needle.
    /// </summary>
    private const string ImportDirectoryNeedle = "/opt/keycloak/data/import";

    private static readonly string CasesFilePath =
        RepoPaths.Find(Path.Combine("tests", "Decisya.Identity.Tests", "realm-guard-cases.json"));

    private static readonly JsonSerializerOptions CasesSerializerOptions =
        new() { PropertyNameCaseInsensitive = true };

    private sealed record CaseRow(
        string Path,
        string Content,
        bool Offends,
        [property: JsonPropertyName("in_scope")] bool InScope);

    private static List<CaseRow> LoadCases()
    {
        var json = File.ReadAllText(CasesFilePath);
        return JsonSerializer.Deserialize<List<CaseRow>>(json, CasesSerializerOptions)
            ?? throw new InvalidOperationException($"{CasesFilePath} deserialized to null.");
    }

    /// <summary>
    /// G6-77-09: the theory is driven by row index only. xUnit's default display name for a
    /// `TheoryData&lt;int&gt;` row would just be the index anyway, but we set an explicit,
    /// path-only display name (never the row's content, which is exactly where the needle
    /// lives) so a `--report-xunit-trx` run cannot write the needle into a `.trx` at the
    /// repository root, which the next plain scan would then flag.
    /// </summary>
    public static TheoryData<int> Cases()
    {
        var rows = LoadCases();
        var data = new TheoryData<int>();

        for (var index = 0; index < rows.Count; index++)
        {
            var row = new TheoryDataRow<int>(index).WithTestDisplayName($"case {index}: {rows[index].Path}");
            data.Add(row);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Offends_matches_the_expected_verdict_for_every_shared_case(int index)
    {
        var row = LoadCases()[index];

        RealmGuard.Offends(row.Path, row.Content).Should().Be(
            row.Offends,
            $"'{row.Path}' should {(row.Offends ? "offend" : "not offend")} per realm-guard-cases.json");
    }

    /// <summary>
    /// G6-77-04: the case table's own `in_scope` label must never drift from what the guard
    /// actually decides. Before this test, a row mislabelled `in_scope: false` would silently
    /// drop out of the scope-⊆-trigger proof without anything failing here.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void In_scope_matches_RealmGuard_unconditional_exemption_for_every_shared_case(int index)
    {
        var row = LoadCases()[index];

        row.InScope.Should().Be(
            !RealmGuard.IsUnconditionallyExempt(row.Path),
            $"'{row.Path}' is in_scope in realm-guard-cases.json iff RealmGuard does not exempt it unconditionally");
    }

    /// <summary>
    /// G4-77-09: the exemption list is pinned. A widened exemption is a G6 BLOCK unless G3
    /// amends docs/security/threat-models/realm-guard-scope.md.
    /// </summary>
    [Fact]
    public void The_exemption_list_is_exactly_the_expected_set()
    {
        string[] expected =
        [
            "src/Decisya.AppHost/AppHost.cs (exact, separator-anchored)",
            "decisya.slnx (exact)",
            "tests/ (prefix, separator-anchored, excluding any .claude/ area, nested included) (G6-77-11)",
            "docs/ (prefix, separator-anchored, excluding any .claude/ area, nested included) (G6-77-11)",
            "LICENSE (exact)",
            ".github/ISSUE_TEMPLATE/ (prefix, separator-anchored)",
            ".github/dependabot.yml (exact)",
            "*.md file NOT under any .claude/ area, nested included (suffix; CI's trigger ignores it too)",
            ".claude/tests/** (nested `<dir>/.claude/tests/` included) without an import marker (G4-77-04)",
            ".claude/**/*.md prose (nested `<dir>/.claude/**/*.md` included), outside YAML frontmatter " +
            "(--- followed only by whitespace, matched like Claude Code's own \\s, including U+00A0 " +
            "and U+FEFF; an unterminated leading block counts to end of file) and outside any inline " +
            "!` or fenced ```! marker anywhere in the file " +
            "(G4-77-02, G6-77-01, G6-77-02, G6-77-03, G6-77-10)",
            // #121, G3 amendment (realm-guard-scope.md, "Amendment at G3 for #121"): the one new rule.
            "deploy/compose/docker-compose.stack.yaml and deploy/compose/stackguards.py (exact paths, no case folding, " +
            "no prefix; neither names the dev realm file) may name the container import directory: the overlay only as " +
            "the exact production file target (/realm-decisya.json then end of line, quote, whitespace or colon), " +
            "stackguards.py only with no launch marker (G4-121-02, #121)",
        ];

        expected.Should().HaveCount(11, "the amendment adds exactly one rule to the ten pinned at e4be09d/7a0297e");

        RealmGuard.ExemptionRules.Select(rule => rule.Description).Should().BeEquivalentTo(
            expected,
            options => options.WithStrictOrdering(),
            RealmGuard.PinnedExemptionRuleDescription);
    }

    /// <summary>
    /// G6-77-06: the meta-test above pins descriptions, not behaviour. A new branch added to
    /// <c>RealmGuard</c>'s decision that leaves the description list untouched would pass that
    /// test. This one proves each rule's predicate is actually load-bearing: for every rule,
    /// the shared case table has at least one row, containing the needle, that only this rule
    /// exempts (remove it, and every other rule still says "offend").
    /// </summary>
    [Fact]
    public void Each_exemption_rule_is_the_sole_reason_at_least_one_case_does_not_offend()
    {
        var rows = LoadCases();
        var rules = RealmGuard.ExemptionRules;

        for (var ruleIndex = 0; ruleIndex < rules.Length; ruleIndex++)
        {
            var rule = rules[ruleIndex];

            var isDecisiveForSomeRow = rows.Any(row =>
                !row.Offends
                && (row.Content.Contains(Needle, StringComparison.OrdinalIgnoreCase)
                    || row.Content.Contains(ImportDirectoryNeedle, StringComparison.OrdinalIgnoreCase))
                && rule.Exempts(row.Path, row.Content)
                && !IsExemptedByAnyOtherRule(rules, ruleIndex, row));

            isDecisiveForSomeRow.Should().BeTrue(
                $"rule '{rule.Description}' must be the sole reason at least one row in " +
                "realm-guard-cases.json does not offend");
        }
    }

    /// <summary>
    /// #121 red-green evidence (realm-guard-scope.md, "Red-green evidence for G4"): without the new
    /// rule, the exact production target in the overlay and the bare constant in stackguards.py
    /// both offend (red); with it they do not (green), and nothing else becomes exempt.
    /// </summary>
    [Fact]
    public void The_121_rule_alone_exempts_exactly_the_overlay_target_row_and_the_stackguards_constant_row()
    {
        var rows = LoadCases();
        var rule = RealmGuard.ExemptionRules[^1];
        var others = RealmGuard.ExemptionRules[..^1];

        var exempted = rows.Where(row => !row.Offends && rule.Exempts(row.Path, row.Content)).ToList();

        exempted.Select(row => row.Path).Should().BeEquivalentTo(
            ["deploy/compose/docker-compose.stack.yaml", "deploy/compose/stackguards.py"]);
        foreach (var row in exempted)
        {
            others.Any(other => other.Exempts(row.Path, row.Content)).Should().BeFalse(
                $"without the #121 rule '{row.Path}' would offend (red)");
            RealmGuard.Offends(row.Path, row.Content).Should().BeFalse("with the #121 rule it does not (green)");
        }

        // The rule never widens the other direction: every case-table row that offends still offends.
        rows.Where(row => row.Offends).Should().OnlyContain(row => RealmGuard.Offends(row.Path, row.Content));
    }

    private static bool IsExemptedByAnyOtherRule(
        RealmGuard.ExemptionRule[] rules, int excludedRuleIndex, CaseRow row)
    {
        for (var i = 0; i < rules.Length; i++)
        {
            if (i != excludedRuleIndex && rules[i].Exempts(row.Path, row.Content))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// G4-17-12 / G4-77-03: the real working-tree scan, driven by <see cref="RealmGuard.Offends"/>
    /// instead of the inline rules the old test carried. Moved from
    /// <c>RealmExportFileTests</c>, which loses this test.
    /// </summary>
    [Fact]
    public void No_file_in_the_working_tree_offends_the_scoped_guard()
    {
        var repoRoot = RepoPaths.Find(string.Empty);
        var offendingFiles = new List<string>();
        var scannedFileCount = 0;

        foreach (var file in Directory.EnumerateFiles(repoRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = ToRepoRelativePath(repoRoot, file);

            // G6-77-07: skip on the path relative to the repository root, not the absolute
            // path. The old check, `absolutePath.Contains(".git" + separator)`, both let a
            // tracked directory named e.g. `deploy/realm.git/` through unscanned (same shape as
            // T77-11) and, worse, skipped every file in the scan whenever the repository itself
            // was cloned under a parent directory ending in `.git`, which would have made this
            // very test pass vacuously.
            if (IsUnderGitDirectory(relativePath) || ScanExclusions.IsExcluded(relativePath))
            {
                continue;
            }

            string content;
            try
            {
                content = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            scannedFileCount++;

            if (RealmGuard.Offends(relativePath, content))
            {
                offendingFiles.Add(relativePath);
            }
        }

        // G6-77-07: a vacuous scan (for example, every file wrongly skipped as ".git") would
        // otherwise report a clean empty offendingFiles list for the wrong reason.
        scannedFileCount.Should().BeGreaterThan(
            50, "the working-tree scan should have read a substantial part of the repository, " +
                "not skipped it vacuously");

        offendingFiles.Should().BeEmpty(RealmGuard.PinnedExemptionRuleDescription);
    }

    /// <summary>Issue #26: the node_modules skip is exact; the SPA's own sources stay in scope.</summary>
    [Fact]
    public void A_file_under_the_SPA_source_folder_is_still_scanned_and_can_offend()
    {
        ScanExclusions.IsExcluded("src/Decisya.Web/src/main.tsx").Should().BeFalse();
        RealmGuard.Offends("src/Decisya.Web/src/main.tsx", "import 'decisya-realm.json'").Should().BeTrue();
        ScanExclusions.IsExcluded("src/Decisya.Web/node_modules/x/index.js").Should().BeTrue();
    }

    private static bool IsUnderGitDirectory(string relativePath) =>
        relativePath == ".git"
        || relativePath.StartsWith(".git/", StringComparison.Ordinal)
        || relativePath.Contains("/.git/", StringComparison.Ordinal);

    private static string ToRepoRelativePath(string repoRoot, string absolutePath) =>
        Path.GetRelativePath(repoRoot, absolutePath).Replace(Path.DirectorySeparatorChar, '/');
}
