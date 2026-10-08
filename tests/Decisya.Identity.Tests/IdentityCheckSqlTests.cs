using System.Text.RegularExpressions;

namespace Decisya.Identity.Tests;

/// <summary>
/// Issue #121, G3 G4-121-05 (c) and T121-12: <c>identity-check.sql</c> runs as the Postgres
/// superuser through psql, where "SELECT only" is not read-only on its own. A committed, reviewed
/// file plus these static checks and a read-only transaction are the control. No Docker; the SQL
/// is executed against a real Keycloak database by <see cref="ProductionIdentityCheckTests"/>.
/// </summary>
public class IdentityCheckSqlTests
{
    private static readonly string[] ForbiddenTokens =
    [
        "COPY", "PROGRAM", "set_config", "lo_", "pg_", "dblink", "nextval", "setval",
    ];

    private static readonly string[] ForbiddenStatementWords =
    [
        "INSERT", "UPDATE", "DELETE", "DROP", "ALTER", "CREATE", "TRUNCATE", "GRANT", "REVOKE", "CALL",
        "DO", "EXECUTE", "LOCK", "VACUUM", "ANALYZE", "SET", "INTO", "COMMIT", "SAVEPOINT", "LISTEN", "NOTIFY", "REINDEX", "CLUSTER",
    ];

    private static string Raw() => File.ReadAllText(RepoPaths.Find(Path.Combine("deploy", "keycloak", "production", "identity-check.sql")));

    [Fact]
    public void No_line_starts_with_a_backslash_so_no_psql_meta_command_can_run()
    {
        foreach (var line in Raw().Split('\n'))
        {
            line.TrimStart().Should().NotStartWith("\\", "a psql meta-command (\\!, \\o, \\copy, \\gexec) runs a command as the superuser");
        }

        Raw().Should().NotContain("\\", "no backslash anywhere: psql also reads it mid-line");
    }

    [Fact]
    public void None_of_the_forbidden_tokens_appears_even_in_a_comment()
    {
        var raw = Raw();

        foreach (var token in ForbiddenTokens)
        {
            raw.Should().NotContainEquivalentOf(token, $"'{token}' can read files, run programs or change state");
        }
    }

    [Fact]
    public void The_file_is_one_read_only_transaction_holding_exactly_one_select()
    {
        var statements = CodeOnly(Raw())
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(statement => Regex.Replace(statement, @"\s+", " "))
            .ToList();

        statements.Should().HaveCount(3);
        statements[0].Should().Be("BEGIN TRANSACTION READ ONLY");
        statements[1].Should().StartWith("SELECT t.line FROM (VALUES");
        statements[2].Should().Be("ROLLBACK");
        statements.Count(statement => statement.StartsWith("SELECT", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public void The_query_uses_no_statement_word_that_changes_state()
    {
        var code = StripStringLiterals(CodeOnly(Raw()));

        foreach (var word in ForbiddenStatementWords)
        {
            Regex.IsMatch(code, $@"\b{word}\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)).Should().BeFalse(
                $"'{word}' is not allowed in a read-only check");
        }
    }

    [Fact]
    public void The_output_is_key_equals_value_lines_built_from_counts_and_booleans_only()
    {
        var code = CodeOnly(Raw());
        var statement = Regex.Replace(code.Split(';')[1], @"\s+", " ").Trim();

        // The outer select list is the one column of finished lines.
        statement.Should().StartWith("SELECT t.line FROM (VALUES");
        statement.Should().EndWith("ORDER BY t.n");

        // Every row is (n, 'snake_case_key=' || <expression>::text): the only text that reaches the
        // output is a literal key, '=' and a value cast from a count, an EXISTS or a comparison.
        var rows = Regex.Matches(statement, @"\((\d+), '([a-z0-9_]+)=' \|\| ", RegexOptions.None, TimeSpan.FromSeconds(2));
        rows.Count.Should().Be(20);
        rows.Select(match => match.Groups[2].Value).Should().OnlyHaveUniqueItems();

        // No selected column is a name, an email, an id or an attribute value.
        Regex.IsMatch(statement, @"SELECT\s+(DISTINCT\s+)?[a-z]+\.(username|email|id|value|first_name|last_name|secret)\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2))
            .Should().BeFalse("the check prints counts and booleans, never a name, an id or a value");
        Regex.IsMatch(statement, @"SELECT\s+(DISTINCT\s+)?(username|email|id|value|secret)\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2))
            .Should().BeFalse();
    }

    [Fact]
    public void The_master_otp_count_does_not_skip_service_accounts()
    {
        var statement = Regex.Replace(CodeOnly(Raw()), @"\s+", " ");
        var start = statement.IndexOf("'master_users_without_otp='", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);

        // The row runs to the next row's "(17," opener; no service-account condition may sit in it
        // (G6 F-01: a master service-account client with an admin role holds full power without OTP).
        var end = statement.IndexOf("(17,", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        var row = statement[start..end];

        row.Should().Contain("r.name = 'master'");
        row.Should().Contain("c.type = 'otp'");
        row.Should().NotContain("service_account_client_link");
    }

    [Fact]
    public void The_file_is_lf_only()
    {
        Raw().Should().NotContain("\r");
    }

    private static string CodeOnly(string sql) =>
        string.Join('\n', sql.Split('\n').Select(line =>
        {
            var commentStart = line.IndexOf("--", StringComparison.Ordinal);
            return commentStart >= 0 ? line[..commentStart] : line;
        }));

    private static string StripStringLiterals(string sql) =>
        Regex.Replace(sql, "'[^']*'", "''", RegexOptions.None, TimeSpan.FromSeconds(2));
}
