using System.Text.RegularExpressions;

namespace Decisya.ArchitectureTests;

/// <summary>
/// Issue #21, G2 ("no file under src/ contains EnableSensitiveDataLogging,
/// EnableDetailedErrors or AddDbContextPool") and G3 G4-21-04 (extends the ban to Npgsql's own
/// detail switches): a static, repo-wide guard that answers #22 B-3's second half and closes
/// T-10. Scans every <c>.cs</c> and <c>.json</c> file under <c>src/</c> — a future module could
/// otherwise flip one of these switches in an <c>appsettings.json</c> connection string or in
/// code without any single module's own tests ever catching it.
/// </summary>
public class NoSensitiveEfSwitchesTests
{
    /// <summary>Case-sensitive: real C# identifiers, never legitimately spelled another way in source.</summary>
    private static readonly string[] BannedIdentifiers =
    [
        "EnableSensitiveDataLogging",
        "EnableDetailedErrors",
        "AddDbContextPool",
        "IncludeErrorDetail",
        "EnableParameterLogging",
    ];

    /// <summary>The Npgsql connection-string keyword (G3): case-insensitive, and spelled with a space in a connection string.</summary>
    private const string BannedConnectionStringKeyword = "Include Error Detail";

    [Fact]
    public void No_file_under_src_enables_a_sensitive_EF_or_Npgsql_diagnostic_switch()
    {
        var srcRoot = RepoPaths.Find("src");
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.*", SearchOption.AllDirectories))
        {
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                !file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rawText = File.ReadAllText(file);

            // A .cs file's own comments may legitimately name a banned identifier to explain
            // why it must never be used (e.g. TenantDbContext's own remarks on
            // AddDbContextPool). Only executable text is checked; .json has no comments to
            // strip.
            var text = file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? StripComments(rawText) : rawText;

            foreach (var identifier in BannedIdentifiers)
            {
                // Word-boundary, not a plain substring match: ASP.NET's own (unrelated, and
                // already correctly used) JwtBearerOptions.IncludeErrorDetails must never trip
                // the ban on Npgsql's distinct, singular IncludeErrorDetail.
                if (Regex.IsMatch(text, $@"\b{Regex.Escape(identifier)}\b", RegexOptions.None))
                {
                    violations.Add($"{file}: {identifier}");
                }
            }

            if (text.Contains(BannedConnectionStringKeyword, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"{file}: {BannedConnectionStringKeyword}");
            }
        }

        violations.Should().BeEmpty();
    }

    /// <summary>Naive but sufficient for this scan: strips <c>//</c> and <c>/* */</c> comments so a doc comment explaining why a switch is banned does not trip the ban itself.</summary>
    private static string StripComments(string source)
    {
        var withoutBlockComments = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlockComments, @"//.*?$", string.Empty, RegexOptions.Multiline);
    }
}
