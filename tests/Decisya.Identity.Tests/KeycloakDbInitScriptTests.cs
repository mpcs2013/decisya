using System.Text.RegularExpressions;

namespace Decisya.Identity.Tests;

/// <summary>
/// G3 T-07 (change 2) / G4-17-15: static checks over the Postgres init script that creates
/// Keycloak's dedicated role and database. These never need Docker. The role's own
/// privilege attributes (no Superuser, no Create DB, no Create role) and PUBLIC's lack of
/// a connect entry are proven live, from inside the running container, by Marco's manual
/// G4-17-16 check (docs/ai/pipeline/17.md), not by this static file test.
/// </summary>
public class KeycloakDbInitScriptTests
{
    private static readonly Regex EchoOrPrintfCommand = new(
        @"(^|[;&|]|\bthen\b|\belse\b)\s*(echo|printf)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string ScriptPath { get; } =
        RepoPaths.Find(Path.Combine("deploy", "postgres", "init", "10-keycloak-db.sh"));

    private static string ReadScriptRaw() => File.ReadAllText(ScriptPath);

    [Fact]
    public void The_script_starts_with_set_dash_eu_right_after_its_shebang_and_comments()
    {
        var lines = ReadScriptRaw().Split('\n');
        var firstExecutableLine = lines.First(line => line.Trim().Length > 0 && !line.TrimStart().StartsWith('#'));

        firstExecutableLine.Trim().Should().Be("set -eu");
    }

    [Fact]
    public void The_script_fails_fast_on_an_unset_password_before_psql_ever_runs()
    {
        var content = ReadScriptRaw();
        var codeOnly = string.Join('\n', NonCommentLines(content));

        codeOnly.Should().Contain(": \"${DECISYA_KEYCLOAK_DB_PASSWORD:?");
        codeOnly.Should().Contain("psql -v");

        var guardIndex = codeOnly.IndexOf(
            ": \"${DECISYA_KEYCLOAK_DB_PASSWORD:?", StringComparison.Ordinal);
        var psqlIndex = codeOnly.IndexOf("psql -v", StringComparison.Ordinal);

        guardIndex.Should().BeGreaterThan(-1);
        psqlIndex.Should().BeGreaterThan(-1);
        guardIndex.Should().BeLessThan(psqlIndex, "the unset-password guard must run before psql is invoked");
    }

    [Fact]
    public void No_line_traces_or_echoes_the_password_and_set_dash_x_never_appears()
    {
        var content = ReadScriptRaw();
        var nonCommentLines = NonCommentLines(content).ToList();

        // The doc comments (both '#' shell comments and '--' SQL comments) name "set -x",
        // "echo" and "printf" in backticks to explain this very rule; they never invoke
        // any of the three, so only non-comment lines are checked below.
        foreach (var line in nonCommentLines)
        {
            line.Should().NotContain("set -x", $"line should not enable shell tracing: '{line}'");
            line.Should().NotContain("set -o xtrace", $"line should not enable shell tracing: '{line}'");
            EchoOrPrintfCommand.IsMatch(line).Should().BeFalse(
                $"line should not invoke echo or printf: '{line}'");
        }
    }

    /// <summary>Lines with neither a '#' shell comment nor a '--' SQL comment as their
    /// first non-blank character, so a rule's own explanatory prose (which names the
    /// forbidden commands and variables to describe the rule) is never mistaken for a
    /// violation of it.</summary>
    private static IEnumerable<string> NonCommentLines(string content) =>
        content.Split('\n').Where(line =>
        {
            var trimmed = line.TrimStart();
            return trimmed.Length == 0
                || (!trimmed.StartsWith('#') && !trimmed.StartsWith("--", StringComparison.Ordinal));
        });

    [Fact]
    public void The_SQL_heredoc_is_single_quoted_so_the_shell_never_expands_it()
    {
        ReadScriptRaw().Should().Contain("<<'EOSQL'");
    }

    [Fact]
    public void The_password_reaches_SQL_only_through_the_getenv_bound_kc_password_variable()
    {
        var content = ReadScriptRaw();

        content.Should().Contain(@"\getenv kc_password DECISYA_KEYCLOAK_DB_PASSWORD");
        content.Should().Contain(":'kc_password'");

        // Outside the \getenv line itself, the raw environment-variable name must never
        // reach the SQL body: only the psql variable :'kc_password' may carry the value.
        var heredocStart = content.IndexOf("<<'EOSQL'", StringComparison.Ordinal);
        var heredocBody = content[heredocStart..];
        var occurrencesOfRawVariableInBody = Regex.Count(
            heredocBody, @"\bDECISYA_KEYCLOAK_DB_PASSWORD\b");

        occurrencesOfRawVariableInBody.Should().Be(
            1, "the raw environment-variable name should appear exactly once in the heredoc, on the \\getenv line");
    }

    [Fact]
    public void The_heredoc_disables_statement_logging_sets_the_role_attributes_and_revokes_public_access()
    {
        var content = ReadScriptRaw();

        content.Should().Contain("SET log_statement = 'none';");
        content.Should().Contain("SET log_min_error_statement = 'panic';");
        content.Should().Contain("NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT");
        content.Should().Contain("CREATE DATABASE keycloak OWNER keycloak;");
        content.Should().Contain("REVOKE ALL ON DATABASE keycloak FROM PUBLIC;");
    }

    [Fact]
    public void The_file_uses_LF_line_endings_only()
    {
        var bytes = File.ReadAllBytes(ScriptPath);
        var content = System.Text.Encoding.UTF8.GetString(bytes);

        content.Should().NotContain("\r");
    }
}
