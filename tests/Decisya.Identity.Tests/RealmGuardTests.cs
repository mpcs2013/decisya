using System.Text.Json;
using System.Text.Json.Serialization;

namespace Decisya.Identity.Tests;

/// <summary>
/// G4-77 (issue #77, G4 part 1 of 3): the scoped realm-file guard. Replaces
/// <c>RealmExportFileTests.The_realm_file_name_is_referenced_only_from_the_AppHost_tests_and_docs</c>
/// (G4-17-12), which used an unanchored absolute-path prefix match (T77-11) and a
/// directory-wide `.claude/` allowance that would have reopened T77-01. No
/// <c>[Trait("Category", "Integration")]</c>: everything here reads files already on disk, no
/// Docker. See docs/security/threat-models/realm-guard-scope.md.
/// </summary>
public class RealmGuardTests
{
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

    public static TheoryData<string, string, bool> Cases()
    {
        var data = new TheoryData<string, string, bool>();

        foreach (var row in LoadCases())
        {
            data.Add(row.Path, row.Content, row.Offends);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Offends_matches_the_expected_verdict_for_every_shared_case(
        string relativePath, string content, bool expectedOffends)
    {
        RealmGuard.Offends(relativePath, content).Should().Be(
            expectedOffends,
            $"'{relativePath}' should {(expectedOffends ? "offend" : "not offend")} per realm-guard-cases.json");
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
            "tests/ (prefix, separator-anchored)",
            "docs/ (prefix, separator-anchored)",
            "LICENSE (exact)",
            ".github/ISSUE_TEMPLATE/ (prefix, separator-anchored)",
            ".github/dependabot.yml (exact)",
            "*.md file NOT under .claude/ (suffix; CI's trigger ignores it too)",
            ".claude/tests/** without an import marker (G4-77-04)",
            ".claude/**/*.md prose outside YAML frontmatter and outside a line containing \"!`\" (G4-77-02)",
        ];

        RealmGuard.ExemptionRules.Should().BeEquivalentTo(
            expected,
            options => options.WithStrictOrdering(),
            RealmGuard.PinnedExemptionRuleDescription);
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

        foreach (var file in Directory.EnumerateFiles(repoRoot, "*", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.Combine(".git") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
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

            var relativePath = ToRepoRelativePath(repoRoot, file);

            if (RealmGuard.Offends(relativePath, content))
            {
                offendingFiles.Add(relativePath);
            }
        }

        offendingFiles.Should().BeEmpty(RealmGuard.PinnedExemptionRuleDescription);
    }

    private static string ToRepoRelativePath(string repoRoot, string absolutePath) =>
        Path.GetRelativePath(repoRoot, absolutePath).Replace(Path.DirectorySeparatorChar, '/');
}
