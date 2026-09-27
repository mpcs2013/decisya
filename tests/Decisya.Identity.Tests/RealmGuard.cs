namespace Decisya.Identity.Tests;

/// <summary>
/// Pure decision function backing <see cref="RealmGuardTests"/> and the repository-wide scan
/// that replaces <c>RealmExportFileTests.The_realm_file_name_is_referenced_only_from_the_AppHost_tests_and_docs</c>
/// (G4-77-03). Kept in the test project, not production code, so the case table
/// (<c>realm-guard-cases.json</c>) can exercise every rule without touching the file system.
/// See docs/security/threat-models/realm-guard-scope.md for the threat model this implements
/// (issue #77, G4 part 1 of 3).
/// </summary>
internal static class RealmGuard
{
    /// <summary>
    /// G4-77-13 (SHOULD, taken): both needles are matched case-insensitively. The file name
    /// itself, and the container import directory that only makes sense in a launch
    /// definition (T77-09).
    /// </summary>
    private static readonly string[] Needles = ["decisya-realm.json", "/opt/keycloak/data/import"];

    /// <summary>G4-77-04: markers that turn a `.claude/tests/**` mention into a launch path.</summary>
    private static readonly string[] ClaudeTestsImportMarkers =
        ["--import-realm", "data/import", "WithRealmImport", "start-dev", "docker", "podman", "testcontainers"];

    /// <summary>
    /// The exemption rules, in the order <see cref="Offends"/> applies them. Exposed as data so
    /// the pinned meta-test (G4-77-09) can assert the exact set: a widened exemption is a G6
    /// BLOCK unless G3 amends the threat delta.
    /// </summary>
    public static readonly string[] ExemptionRules =
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

    /// <summary>
    /// The failure message for the pinned meta-test (G4-77-09), naming the rule so a reviewer
    /// diffing G6 sees exactly what a widened exemption would reopen.
    /// </summary>
    public const string PinnedExemptionRuleDescription =
        "only AppHost.cs, decisya.slnx, tests/, docs/, LICENSE, issue templates, dependabot.yml, " +
        "Markdown outside .claude/, .claude/tests/ (without import markers) and .claude/**/*.md " +
        "prose may name the realm file; a new launch path needs #29";

    /// <summary>
    /// True when <paramref name="content"/>, read from the repository-relative, '/'-separated
    /// <paramref name="relativePath"/>, names the realm file or its container import directory
    /// outside every exemption above.
    /// </summary>
    public static bool Offends(string relativePath, string content)
    {
        if (!ContainsAnyNeedle(content))
        {
            return false;
        }

        if (IsUnconditionallyExempt(relativePath))
        {
            return false;
        }

        if (IsUnderClaudeTests(relativePath))
        {
            // G4-77-04: exempt unless the content also carries an import marker.
            return ContainsAnyMarker(content);
        }

        if (IsClaudeMarkdown(relativePath))
        {
            // G4-77-02: exempt unless a needle match sits in frontmatter or on a "!`" line.
            return HasAnOffendingMatch(content);
        }

        return true;
    }

    private static bool ContainsAnyNeedle(string content) =>
        Needles.Any(needle => content.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAnyMarker(string content) =>
        ClaudeTestsImportMarkers.Any(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool IsUnderClaudeTests(string relativePath) =>
        relativePath.StartsWith(".claude/tests/", StringComparison.Ordinal);

    private static bool IsClaudeMarkdown(string relativePath) =>
        relativePath.StartsWith(".claude/", StringComparison.Ordinal)
        && relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnconditionallyExempt(string relativePath)
    {
        if (string.Equals(relativePath, "src/Decisya.AppHost/AppHost.cs", StringComparison.Ordinal)
            || string.Equals(relativePath, "decisya.slnx", StringComparison.Ordinal)
            || string.Equals(relativePath, "LICENSE", StringComparison.Ordinal)
            || string.Equals(relativePath, ".github/dependabot.yml", StringComparison.Ordinal))
        {
            return true;
        }

        if (relativePath.StartsWith("tests/", StringComparison.Ordinal)
            || relativePath.StartsWith("docs/", StringComparison.Ordinal)
            || relativePath.StartsWith(".github/ISSUE_TEMPLATE/", StringComparison.Ordinal))
        {
            return true;
        }

        // CI's trigger ignores every Markdown file outside .claude/ (its ignore regex is
        // `\.md$`, unanchored to a directory), so the guard must exempt the same set to keep
        // scope <= trigger (G4-77-05, G4-77-07). Markdown under .claude/ has its own,
        // narrower, rule below (G4-77-02).
        if (relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            && !relativePath.StartsWith(".claude/", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when at least one needle match, in a `.claude/**/*.md` file, sits inside the
    /// leading YAML frontmatter block or on a line containing <c>!`</c> (an inline-shell
    /// marker Claude Code executes when the skill loads). Both are launch-capable; plain
    /// prose is not (G4-77-02).
    /// </summary>
    private static bool HasAnOffendingMatch(string content)
    {
        var lines = SplitLines(content);
        var frontmatterEndLine = FindFrontmatterEndLine(lines);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!ContainsAnyNeedle(line))
            {
                continue;
            }

            var inFrontmatter = frontmatterEndLine >= 0 && i <= frontmatterEndLine;
            var onInlineShellLine = line.Contains("!`", StringComparison.Ordinal);

            if (inFrontmatter || onInlineShellLine)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The 0-based index of the closing <c>---</c> line, when the first line is exactly
    /// <c>---</c> and a later line is also exactly <c>---</c>; otherwise -1 (no frontmatter).
    /// </summary>
    private static int FindFrontmatterEndLine(string[] lines)
    {
        if (lines.Length == 0 || lines[0] != "---")
        {
            return -1;
        }

        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i] == "---")
            {
                return i;
            }
        }

        return -1;
    }

    private static string[] SplitLines(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
}
