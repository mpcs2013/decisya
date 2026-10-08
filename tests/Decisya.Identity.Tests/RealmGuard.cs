namespace Decisya.Identity.Tests;

/// <summary>
/// Pure decision function backing <see cref="RealmGuardTests"/> and the repository-wide scan
/// that replaces <c>RealmExportFileTests.The_realm_file_name_is_referenced_only_from_the_AppHost_tests_and_docs</c>
/// (G4-77-03). Kept in the test project, not production code, so the case table
/// (<c>realm-guard-cases.json</c>) can exercise every rule without touching the file system.
/// See docs/security/threat-models/realm-guard-scope.md for the threat model this implements
/// (issue #77, G4 part 1 of 3) and docs/security/reviews/77.md for the G6 findings
/// (G6-77-01, 02, 03, 04, 06, 07, 10, 11) fixed here.
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
    /// One exemption rule: a human-readable description (pinned by the G4-77-09 meta-test, and
    /// asserted decisive by G6-77-06) and the predicate <see cref="Offends"/> itself applies, so
    /// the pinned list and the real decision can never drift apart. A predicate takes the
    /// repository-relative, '/'-separated path and the file content, and returns true when this
    /// rule, on its own, exempts the file.
    /// </summary>
    internal sealed record ExemptionRule(string Description, Func<string, string, bool> Exempts);

    /// <summary>
    /// Rules that decide purely from the path: they exempt a file regardless of content, so a
    /// widened branch here can never be masked by a co-occurrence check. Backs
    /// <see cref="IsUnconditionallyExempt"/> (G6-77-04).
    /// </summary>
    private static readonly ExemptionRule[] UnconditionalExemptionRules =
    [
        new(
            "src/Decisya.AppHost/AppHost.cs (exact, separator-anchored)",
            (path, _) => string.Equals(path, "src/Decisya.AppHost/AppHost.cs", StringComparison.Ordinal)),
        new(
            "decisya.slnx (exact)",
            (path, _) => string.Equals(path, "decisya.slnx", StringComparison.Ordinal)),
        new(
            "tests/ (prefix, separator-anchored, excluding any .claude/ area, nested included) (G6-77-11)",
            (path, _) => path.StartsWith("tests/", StringComparison.Ordinal) && !IsUnderClaudeArea(path)),
        new(
            "docs/ (prefix, separator-anchored, excluding any .claude/ area, nested included) (G6-77-11)",
            (path, _) => path.StartsWith("docs/", StringComparison.Ordinal) && !IsUnderClaudeArea(path)),
        new(
            "LICENSE (exact)",
            (path, _) => string.Equals(path, "LICENSE", StringComparison.Ordinal)),
        new(
            ".github/ISSUE_TEMPLATE/ (prefix, separator-anchored)",
            (path, _) => path.StartsWith(".github/ISSUE_TEMPLATE/", StringComparison.Ordinal)),
        new(
            ".github/dependabot.yml (exact)",
            (path, _) => string.Equals(path, ".github/dependabot.yml", StringComparison.Ordinal)),
        new(
            "*.md file NOT under any .claude/ area, nested included (suffix; CI's trigger ignores it too)",
            (path, _) => path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !IsUnderClaudeArea(path)),
    ];

    /// <summary>
    /// Rules that exempt only conditionally on content. Both stay in scope (G4-77-07): a row
    /// covered here is never unconditionally exempt (G6-77-04).
    /// </summary>
    private static readonly ExemptionRule[] ConditionalExemptionRules =
    [
        new(
            ".claude/tests/** (nested `<dir>/.claude/tests/` included) without an import marker (G4-77-04)",
            (path, content) => IsUnderClaudeTests(path) && !ContainsAnyMarker(content)),
        new(
            ".claude/**/*.md prose (nested `<dir>/.claude/**/*.md` included), outside YAML frontmatter " +
            "(--- followed only by whitespace, matched like Claude Code's own \\s, including U+00A0 " +
            "and U+FEFF; an unterminated leading block counts to end of file) and outside any inline " +
            "!` or fenced ```! marker anywhere in the file " +
            "(G4-77-02, G6-77-01, G6-77-02, G6-77-03, G6-77-10)",
            (path, content) => IsClaudeMarkdown(path) && !HasAnOffendingMatch(content)),
        new(
            StackImportReferenceDescription,
            IsStackImportReference),
    ];

    /// <summary>
    /// The exemption rules, in the order <see cref="Offends"/> applies them. Exposed as data so
    /// the pinned meta-test (G4-77-09) can assert the exact set, and so G6-77-06's meta-test can
    /// prove each rule is the sole reason at least one shared-case-table row does not offend: a
    /// widened exemption is a G6 BLOCK unless G3 amends the threat delta.
    /// </summary>
    internal static readonly ExemptionRule[] ExemptionRules =
        [.. UnconditionalExemptionRules, .. ConditionalExemptionRules];

    /// <summary>
    /// The failure message for the pinned meta-test (G4-77-09), naming the rule so a reviewer
    /// diffing G6 sees exactly what a widened exemption would reopen.
    /// </summary>
    public const string PinnedExemptionRuleDescription =
        "only AppHost.cs, decisya.slnx, tests/, docs/, LICENSE, issue templates, dependabot.yml, " +
        "Markdown outside any .claude/ area (nested included), .claude/tests/ (nested included, " +
        "without import markers) and .claude/**/*.md prose (nested included) may name the realm " +
        "file; deploy/compose/docker-compose.stack.yaml and deploy/compose/stackguards.py (exact; " +
        "no dev realm name; overlay: only the exact production file target; stackguards.py: no " +
        "launch marker) may name the import directory; a new launch path needs #29";

    private const string StackOverlayPath = "deploy/compose/docker-compose.stack.yaml";

    private const string StackGuardsPath = "deploy/compose/stackguards.py";

    private const string ImportDirectory = "/opt/keycloak/data/import";

    private const string ProductionRealmTarget = "/realm-decisya.json";

    private const string DevRealmFileName = "decisya-realm.json";

    /// <summary>The pinned description of the #121 rule (docs/security/threat-models/realm-guard-scope.md, "Amendment at G3 for #121").</summary>
    internal const string StackImportReferenceDescription =
        "deploy/compose/docker-compose.stack.yaml and deploy/compose/stackguards.py (exact paths, no case folding, " +
        "no prefix; neither names the dev realm file) may name the container import directory: the overlay only as " +
        "the exact production file target (/realm-decisya.json then end of line, quote, whitespace or colon), " +
        "stackguards.py only with no launch marker (G4-121-02, #121)";

    /// <summary>Characters that may follow the production file name in the overlay: they cannot continue a path.</summary>
    private static readonly char[] TargetTerminators = ['\n', '\r', '"', '\'', ' ', '\t', ':'];

    /// <summary>Launch markers that make a mention of the import directory in <c>stackguards.py</c> a launch path. The guard checks; it must never launch.</summary>
    private static readonly string[] StackGuardsLaunchMarkers =
        ["subprocess", "os.system", "os.exec", "os.spawn", "popen", "--import-realm", "start-dev"];

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

        return !ExemptionRules.Any(rule => rule.Exempts(relativePath, content));
    }

    /// <summary>
    /// G6-77-04: true when <paramref name="relativePath"/> is exempt regardless of content. The
    /// two content-dependent rules (`.claude/tests` and `.claude` Markdown) are deliberately
    /// excluded: they stay in scope even when today's content happens not to offend. Used by the
    /// shared case table's <c>in_scope</c> assertion, so that label can never drift from the
    /// guard's own decision.
    /// </summary>
    internal static bool IsUnconditionallyExempt(string relativePath) =>
        UnconditionalExemptionRules.Any(rule => rule.Exempts(relativePath, string.Empty));

    /// <summary>
    /// The #121 rule (G3 amendment): exempts exactly the two stack files, on their exact
    /// repository-relative paths, and only while they do not name the dev realm file. The overlay
    /// may name the import directory only as the exact production file target; stackguards.py only
    /// with no launch marker. Anything else, a bare or trailing-slash directory, another file name,
    /// a case variant, a nested copy, still offends.
    /// </summary>
    private static bool IsStackImportReference(string relativePath, string content)
    {
        var isOverlay = string.Equals(relativePath, StackOverlayPath, StringComparison.Ordinal);
        var isGuards = string.Equals(relativePath, StackGuardsPath, StringComparison.Ordinal);
        if (!isOverlay && !isGuards)
        {
            return false;
        }

        if (content.Contains(DevRealmFileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (isGuards)
        {
            return !StackGuardsLaunchMarkers.Any(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase));
        }

        var searchFrom = 0;
        while (true)
        {
            var index = content.IndexOf(ImportDirectory, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return true;
            }

            var afterDirectory = index + ImportDirectory.Length;
            if (string.CompareOrdinal(content, afterDirectory, ProductionRealmTarget, 0, ProductionRealmTarget.Length) != 0)
            {
                return false;
            }

            var afterFile = afterDirectory + ProductionRealmTarget.Length;
            if (afterFile < content.Length && Array.IndexOf(TargetTerminators, content[afterFile]) < 0)
            {
                return false;
            }

            searchFrom = afterFile;
        }
    }

    private static bool ContainsAnyNeedle(string content) =>
        Needles.Any(needle => content.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAnyMarker(string content) =>
        ClaudeTestsImportMarkers.Any(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when <paramref name="relativePath"/> has a `.claude/` segment, at the repository
    /// root or nested under any directory (G6-77-03: equivalent to the regex `(^|/)\.claude/`).
    /// <paramref name="underClaudePath"/> is the remainder of the path after that segment.
    /// </summary>
    private static bool TryGetClaudeRelativePath(string relativePath, out string underClaudePath)
    {
        const string marker = ".claude/";

        if (relativePath.StartsWith(marker, StringComparison.Ordinal))
        {
            underClaudePath = relativePath[marker.Length..];
            return true;
        }

        var nestedIndex = relativePath.IndexOf("/" + marker, StringComparison.Ordinal);
        if (nestedIndex >= 0)
        {
            underClaudePath = relativePath[(nestedIndex + 1 + marker.Length)..];
            return true;
        }

        underClaudePath = string.Empty;
        return false;
    }

    private static bool IsUnderClaudeArea(string relativePath) =>
        TryGetClaudeRelativePath(relativePath, out _);

    private static bool IsUnderClaudeTests(string relativePath) =>
        TryGetClaudeRelativePath(relativePath, out var underClaudePath)
        && underClaudePath.StartsWith("tests/", StringComparison.Ordinal);

    private static bool IsClaudeMarkdown(string relativePath) =>
        IsUnderClaudeArea(relativePath) && relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a needle match, in a `.claude/**/*.md` file (nested included), sits inside the
    /// leading YAML frontmatter block, or the file carries an inline !` or fenced ```! marker
    /// anywhere (G4-77-02, G6-77-01, G6-77-02). The frontmatter check stays line-based: only
    /// content inside that block is launch-capable, through `hooks:`/`tools:`. The shell markers
    /// are checked at file level, not per line (G6-77-01): Claude Code's own parsers match a
    /// fenced ```! block, and an inline !`...` span, across a newline, so a needle on the "next"
    /// line is still executable and must not read as plain prose.
    /// </summary>
    private static bool HasAnOffendingMatch(string content)
    {
        var lines = SplitLines(content);
        var frontmatterEndLine = FindFrontmatterEndLine(lines);

        if (frontmatterEndLine >= 0)
        {
            for (var i = 0; i <= frontmatterEndLine; i++)
            {
                if (ContainsAnyNeedle(lines[i]))
                {
                    return true;
                }
            }
        }

        return ContainsAnyNeedle(content)
            && (content.Contains("!`", StringComparison.Ordinal)
                || content.Contains("```!", StringComparison.Ordinal));
    }

    /// <summary>
    /// The 0-based index of the last line of the leading frontmatter block. The first line
    /// must be a frontmatter delimiter (<see cref="IsFrontmatterDelimiter"/>). If a later line
    /// is also one, that line closes the block. Otherwise (no closing delimiter) the block is
    /// unterminated and runs to the end of the file (G6-77-02). Returns -1 when the first line
    /// is not a frontmatter delimiter at all (no leading frontmatter).
    /// </summary>
    private static int FindFrontmatterEndLine(string[] lines)
    {
        if (lines.Length == 0 || !IsFrontmatterDelimiter(lines[0]))
        {
            return -1;
        }

        for (var i = 1; i < lines.Length; i++)
        {
            if (IsFrontmatterDelimiter(lines[i]))
            {
                return i;
            }
        }

        return lines.Length - 1;
    }

    /// <summary>
    /// True when <paramref name="line"/> is <c>---</c> followed only by whitespace (G6-77-02),
    /// matched the way Claude Code's own frontmatter regex (<c>^---\s*\n</c>) does rather than by
    /// .NET's narrower notion of whitespace (G6-77-10): JavaScript's <c>\s</c> also accepts
    /// U+00A0 (NBSP) and U+FEFF (BOM / zero-width no-break space), neither of which is trailing
    /// space or a tab, and U+FEFF is not <see cref="char.IsWhiteSpace(char)"/> in .NET either.
    /// </summary>
    private static bool IsFrontmatterDelimiter(string line)
    {
        if (!line.StartsWith("---", StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = 3; i < line.Length; i++)
        {
            if (!IsFrontmatterWhitespace(line[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFrontmatterWhitespace(char c) => char.IsWhiteSpace(c) || c == '﻿';

    private static string[] SplitLines(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
}
