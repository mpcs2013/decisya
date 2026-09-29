using System.Text.RegularExpressions;
using Decisya.Modules.Tenancy;
using Decisya.Modules.Tenancy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Decisya.Infrastructure.Migrator;

/// <summary>
/// Migrates <see cref="TenancyDbContext"/> and provisions the module's least-privilege
/// database role (issue #21, G2, D4; G3 G4-21-05). Idempotent: every step re-runs safely on
/// every start, including against Marco's existing persistent volume and a shared test
/// container where the cluster-wide role may already exist.
/// </summary>
public static partial class MigrationRunner
{
    /// <summary>G3 G4-21-05: validated before the password is used for anything. Never the value, only the key, appears in a failure message.</summary>
    [GeneratedRegex(@"\A[A-Za-z0-9]{32,}\z")]
    private static partial Regex PasswordShapeRegex();

    /// <summary>
    /// Runs the module's migration then role provisioning. Throws
    /// <see cref="InvalidOperationException"/>, naming the missing or invalid configuration
    /// key and never its value, when <paramref name="ownerConnectionString"/> is null or blank,
    /// or <paramref name="tenancyRolePassword"/> is null, blank, shorter than 32 characters, or
    /// contains anything outside <c>[A-Za-z0-9]</c> (a quote in particular could otherwise
    /// break the generated <c>ALTER ROLE</c> statement's quoting).
    /// </summary>
    public static async Task RunAsync(
        string? ownerConnectionString, string? tenancyRolePassword, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ownerConnectionString))
        {
            throw new InvalidOperationException("Missing required configuration key: ConnectionStrings:decisya.");
        }

        if (string.IsNullOrWhiteSpace(tenancyRolePassword) || !PasswordShapeRegex().IsMatch(tenancyRolePassword))
        {
            throw new InvalidOperationException(
                "Missing or invalid required configuration key: Migrator:TenancyRolePassword " +
                "(must be at least 32 alphanumeric characters).");
        }

        var optionsBuilder = new DbContextOptionsBuilder<TenancyDbContext>();
        TenancyDbContextOptions.Configure(optionsBuilder, ownerConnectionString);

        await using (var db = new TenancyDbContext(optionsBuilder.Options, new MigratorInvalidCurrentTenant()))
        {
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }

        await ProvisionTenancyRoleAsync(ownerConnectionString, tenancyRolePassword, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates (if missing), (re-)passwords and grants <see cref="TenancyModule.DatabaseRole"/>
    /// over a plain <see cref="NpgsqlConnection"/> — no EF raw SQL, and this project sits
    /// outside <c>Decisya.ArchitectureTests.ArchitectureScope</c> (G2). Every identifier is
    /// quoted with <see cref="QuoteIdentifier"/>, never interpolated raw.
    /// The password never appears in any statement: <see cref="ScramSha256Verifier.Compute"/>
    /// derives a <c>SCRAM-SHA-256</c> verifier first (G4-21-05's preferred mitigation), and only
    /// that verifier is ever placed in SQL text, itself built server-side via <c>format(%L)</c>
    /// so this method never string-interpolates it either.
    /// </summary>
    private static async Task ProvisionTenancyRoleAsync(
        string ownerConnectionString, string password, CancellationToken cancellationToken)
    {
        const string role = TenancyModule.DatabaseRole;
        const string schema = TenancyModule.Schema;
        const string migrationsHistoryTable = "__EFMigrationsHistory";

        var verifier = ScramSha256Verifier.Compute(password);

        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var database = connection.Database ?? throw new InvalidOperationException(
            "ConnectionStrings:decisya must name a database.");

        var quotedRole = QuoteIdentifier(role);
        var quotedDatabase = QuoteIdentifier(database);
        var quotedSchema = QuoteIdentifier(schema);
        var quotedHistoryTable = $"{quotedSchema}.{QuoteIdentifier(migrationsHistoryTable)}";

        try
        {
            await ExecuteAsync(connection, $"CREATE ROLE {quotedRole}", cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DuplicateObject)
        {
            // Idempotent: roles are cluster-wide, so a shared test container or a re-run
            // against Marco's existing volume finds it already there.
        }

        var alterRoleSql = await BuildFormattedSqlAsync(
            connection,
            "format('ALTER ROLE %I WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD %L', @role, @verifier)",
            [("role", role), ("verifier", verifier)],
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, alterRoleSql, cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, $"REVOKE ALL ON DATABASE {quotedDatabase} FROM PUBLIC", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, $"GRANT CONNECT ON DATABASE {quotedDatabase} TO {quotedRole}", cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, $"REVOKE ALL ON SCHEMA {quotedSchema} FROM PUBLIC", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, $"GRANT USAGE ON SCHEMA {quotedSchema} TO {quotedRole}", cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, $"GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {quotedSchema} TO {quotedRole}", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, $"REVOKE ALL ON {quotedHistoryTable} FROM {quotedRole}", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Postgres's own identifier-quoting rule: wrap in double quotes, doubling any embedded double quote. Every identifier here is a compile-time module constant or the connection's own database name — never request input.</summary>
    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>Asks Postgres itself to build a properly-escaped statement via <c>format()</c>, with every substituted value passed as a bound parameter — never interpolated client-side (G2).</summary>
#pragma warning disable CA2100 // formatExpression is a fixed literal built from compile-time constants above; the sensitive values travel as bound parameters, never interpolated.
    private static async Task<string> BuildFormattedSqlAsync(
        NpgsqlConnection connection, string formatExpression, (string Name, string Value)[] parameters, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT {formatExpression}", connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return (string)result!;
    }
#pragma warning restore CA2100

    /// <summary>Every caller of this method passes SQL built above from module constants and <see cref="QuoteIdentifier"/>-quoted values, or the server-built statement from <see cref="BuildFormattedSqlAsync"/> — never request input.</summary>
#pragma warning disable CA2100
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
#pragma warning restore CA2100
}
