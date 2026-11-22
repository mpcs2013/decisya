using System.Text.RegularExpressions;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #341, G6-341-04, G3 F341-01, G2 R-L21, ADR-0013 amendment 1 point 2: each module appends only its own audit
/// action family. Enum values are inlined into the IL, so this is a source scan (the R-L6 approach): under <c>src/</c>,
/// <c>AuditAction.Ledger*</c> appears only in <c>src/Modules/Ledger/**</c> and <c>AuditAction.Entitlements*</c> only in
/// <c>src/Modules/Entitlements/**</c>. The Audit module (<c>src/Modules/Audit/**</c>) is exempt, because it defines and
/// maps the values. A <c>using static</c> of the enum outside the Audit module hides the qualifier, so it is a violation
/// in any module. Whole-line <c>//</c> comments are ignored.
/// </summary>
public static partial class AuditActionFamilyRule
{
    private const string AuditPath = "src/Modules/Audit/";

    /// <summary>The families: the action-name prefix and the one folder allowed to use it.</summary>
    private static readonly (string Prefix, string Folder)[] Families =
    [
        ("Ledger", "src/Modules/Ledger/"),
        ("Entitlements", "src/Modules/Entitlements/"),
    ];

    [GeneratedRegex(@"\bAuditAction\s*\.\s*(?<member>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex Qualified();

    [GeneratedRegex(@"\busing\s+static\s+[\w.]*\bAuditAction\s*;", RegexOptions.CultureInvariant)]
    private static partial Regex UsingStatic();

    /// <summary>One line per violation for the given repository-relative, '/'-separated files and their text.</summary>
    public static IReadOnlyList<string> Scan(IEnumerable<(string RelativePath, string Text)> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var violations = new List<string>();
        foreach (var (path, text) in files)
        {
            if (path.StartsWith(AuditPath, StringComparison.Ordinal))
            {
                continue;
            }

            var code = string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            if (UsingStatic().IsMatch(code))
            {
                violations.Add($"{path}: 'using static' of AuditAction hides the family");
            }

            foreach (Match match in Qualified().Matches(code))
            {
                var member = match.Groups["member"].Value;
                foreach (var (prefix, folder) in Families)
                {
                    if (member.StartsWith(prefix, StringComparison.Ordinal) && !path.StartsWith(folder, StringComparison.Ordinal))
                    {
                        violations.Add($"{path}: AuditAction.{member} is outside {folder}");
                    }
                }
            }
        }

        return violations;
    }

    /// <summary>Every <c>*.cs</c> file under <c>src/</c> (not <c>bin</c> or <c>obj</c>), with its repository-relative path.</summary>
    public static IEnumerable<(string RelativePath, string Text)> SourceFiles()
    {
        var root = RepoPaths.Find(".");
        var src = RepoPaths.Find("src");

        foreach (var file in ScanExclusions.EnumerateFiles(root, src, "*.cs"))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            var segments = relative.Split('/');
            if (segments.Contains("bin", StringComparer.Ordinal) || segments.Contains("obj", StringComparer.Ordinal))
            {
                continue;
            }

            yield return (relative, File.ReadAllText(file));
        }
    }
}
