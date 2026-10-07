using System.Text.RegularExpressions;
using Decisya.Modules.Audit;
using Decisya.Modules.Audit.Infrastructure;
using Decisya.Modules.Entitlements;
using Decisya.Modules.Entitlements.Infrastructure;
using Decisya.Modules.Tenancy;
using Decisya.Modules.Tenancy.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Decisya.Infrastructure.Migrator;

/// <summary>
/// Migrates <see cref="TenancyDbContext"/>, <see cref="EntitlementsDbContext"/> and
/// <see cref="AuditDbContext"/> and provisions each module's least-privilege database role (issue #21, G2, D4; G3 G4-21-05;
/// issue #23, G4-23-04), then the append-only grants on the audit table (issue #24, ADR-0013, G4-24-01). Idempotent: every step re-runs safely on every start, including
/// against Marco's existing persistent volume and a shared test container where a
/// cluster-wide role may already exist.
/// </summary>
public static partial class MigrationRunner
{
    private const string TenancyPasswordKey = "Migrator:TenancyRolePassword";
    private const string EntitlementsPasswordKey = "Migrator:EntitlementsRolePassword";

    /// <summary>G3 G4-21-05: validated before the password is used for anything. Never the value, only the key, appears in a failure message.</summary>
    [GeneratedRegex(@"\A[A-Za-z0-9]{32,}\z")]
    private static partial Regex PasswordShapeRegex();

    /// <summary>
    /// Runs each module's migration then its role provisioning (Tenancy, then Entitlements), then the Audit migration and its append-only grant step.
    /// Throws <see cref="InvalidOperationException"/>, naming the missing or invalid
    /// configuration key and never its value, and before any connection opens, when
    /// <paramref name="ownerConnectionString"/> is null or blank, or either role password is
    /// null, blank, shorter than 32 characters, or contains anything outside
    /// <c>[A-Za-z0-9]</c> (a quote in particular could otherwise break the generated
    /// <c>ALTER ROLE</c> statement's quoting).
    /// </summary>
    public static async Task RunAsync(
        string? ownerConnectionString,
        string? tenancyRolePassword,
        string? entitlementsRolePassword,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ownerConnectionString))
        {
            throw new InvalidOperationException("Missing required configuration key: ConnectionStrings:decisya.");
        }

        ValidatePassword(tenancyRolePassword, TenancyPasswordKey);
        ValidatePassword(entitlementsRolePassword, EntitlementsPasswordKey);

        var tenancyOptions = new DbContextOptionsBuilder<TenancyDbContext>();
        TenancyDbContextOptions.Configure(tenancyOptions, ownerConnectionString);

        await using (var db = new TenancyDbContext(tenancyOptions.Options, new MigratorInvalidCurrentTenant()))
        {
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }

        // Each module's grants run after that module's own migration, so ON ALL TABLES sees its tables.
        await ProvisionModuleRoleAsync(
            ownerConnectionString, TenancyModule.DatabaseRole, TenancyModule.Schema, tenancyRolePassword!, cancellationToken)
            .ConfigureAwait(false);

        var entitlementsOptions = new DbContextOptionsBuilder<EntitlementsDbContext>();
        EntitlementsDbContextOptions.Configure(entitlementsOptions, ownerConnectionString);

        await using (var db = new EntitlementsDbContext(entitlementsOptions.Options, new MigratorInvalidCurrentTenant()))
        {
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }

        await ProvisionModuleRoleAsync(
            ownerConnectionString, EntitlementsModule.DatabaseRole, EntitlementsModule.Schema, entitlementsRolePassword!, cancellationToken)
            .ConfigureAwait(false);

        // ADR-0013: Audit has no login role of its own. Its table is created after the roles above
        // exist, and the grant step narrows every module role before it grants INSERT to the writer.
        var auditOptions = new DbContextOptionsBuilder<AuditDbContext>();
        AuditDbContextOptions.Configure(auditOptions, ownerConnectionString);

        await using (var db = new AuditDbContext(auditOptions.Options, new MigratorInvalidCurrentTenant()))
        {
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }

        await ProvisionAppendOnlyGrantsAsync(
            ownerConnectionString,
            AuditModule.Schema,
            AuditModule.RecordsTable,
            writerRoles: [EntitlementsModule.DatabaseRole],
            moduleRoles: [TenancyModule.DatabaseRole, EntitlementsModule.DatabaseRole],
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidatePassword(string? password, string configurationKey)
    {
        if (string.IsNullOrWhiteSpace(password) || !PasswordShapeRegex().IsMatch(password))
        {
            throw new InvalidOperationException(
                $"Missing or invalid required configuration key: {configurationKey} " +
                "(must be at least 32 alphanumeric characters).");
        }
    }

    /// <summary>
    /// Creates (if missing), (re-)passwords and grants one module's database role
    /// (<paramref name="role"/>, on <paramref name="schema"/> only) over a plain <see cref="NpgsqlConnection"/> — no EF raw SQL, and this project sits
    /// outside <c>Decisya.ArchitectureTests.ArchitectureScope</c> (G2). Every identifier is
    /// quoted with <see cref="QuoteIdentifier"/>, never interpolated raw.
    /// The password never appears in any statement: <see cref="ScramSha256Verifier.Compute"/>
    /// derives a <c>SCRAM-SHA-256</c> verifier first (G4-21-05's preferred mitigation), and only
    /// that verifier is ever placed in SQL text, itself built server-side via <c>format(%L)</c>
    /// so this method never string-interpolates it either.
    /// </summary>
    private static async Task ProvisionModuleRoleAsync(
        string ownerConnectionString, string role, string schema, string password, CancellationToken cancellationToken)
    {
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
            // C-06: CREATE checks only attributes set to true, so a non-superuser CREATEROLE
            // migrator may spell out the full no-privilege list here.
            await ExecuteAsync(
                connection,
                $"CREATE ROLE {quotedRole} WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS",
                cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DuplicateObject)
        {
            // Idempotent: roles are cluster-wide, so a shared test container or a re-run
            // against Marco's existing volume finds it already there.
        }

        // Postgres 16+: ALTER ROLE may name SUPERUSER, CREATEDB, REPLICATION or BYPASSRLS (even as NO...)
        // only for a role that holds that attribute, so a non-superuser migrator cannot list them here.
        // The read-back below fails closed instead.
        var alterRoleSql = await BuildFormattedSqlAsync(
            connection,
            "format('ALTER ROLE %I WITH LOGIN NOINHERIT NOCREATEROLE PASSWORD %L', @role, @verifier)",
            [("role", role), ("verifier", verifier)],
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, alterRoleSql, cancellationToken).ConfigureAwait(false);

        await VerifyRoleHasNoElevatedAttributesAsync(connection, role, cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, $"REVOKE ALL ON DATABASE {quotedDatabase} FROM PUBLIC", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, $"GRANT CONNECT ON DATABASE {quotedDatabase} TO {quotedRole}", cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, $"REVOKE ALL ON SCHEMA {quotedSchema} FROM PUBLIC", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, $"GRANT USAGE ON SCHEMA {quotedSchema} TO {quotedRole}", cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, $"GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {quotedSchema} TO {quotedRole}", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, $"REVOKE ALL ON {quotedHistoryTable} FROM {quotedRole}", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the role's attributes back from <c>pg_roles</c> and fails closed (issue #120, C-06, G4-120-04).</summary>
    private static async Task VerifyRoleHasNoElevatedAttributesAsync(
        NpgsqlConnection connection, string role, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT rolsuper, rolcreatedb, rolcreaterole, rolreplication, rolbypassrls, rolinherit, rolcanlogin FROM pg_roles WHERE rolname = @role",
            connection);
        command.Parameters.AddWithValue("role", role);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Database role '{role}' does not exist after provisioning.");
        }

        EnsureNoElevatedAttributes(
            role,
            new RoleAttributes(
                reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3),
                reader.GetBoolean(4), reader.GetBoolean(5), reader.GetBoolean(6)));
    }

    /// <summary>Attributes of a role as read from <c>pg_roles</c>.</summary>
    internal readonly record struct RoleAttributes(
        bool Super, bool CreateDb, bool CreateRole, bool Replication, bool BypassRls, bool Inherit, bool CanLogin);

    /// <summary>Throws naming the role and the first offending attribute, never a secret.</summary>
    internal static void EnsureNoElevatedAttributes(string role, RoleAttributes attributes)
    {
        string? offending =
            attributes.Super ? "SUPERUSER" :
            attributes.CreateDb ? "CREATEDB" :
            attributes.CreateRole ? "CREATEROLE" :
            attributes.Replication ? "REPLICATION" :
            attributes.BypassRls ? "BYPASSRLS" :
            attributes.Inherit ? "INHERIT" :
            !attributes.CanLogin ? "NOLOGIN" :
            null;

        if (offending is not null)
        {
            throw new InvalidOperationException(
                $"Database role '{role}' has an unexpected attribute ({offending}); refusing to continue. " +
                "A role created outside the migrator must be corrected by a superuser.");
        }
    }

    /// <summary>
    /// Makes <paramref name="schema"/>.<paramref name="table"/> append-only for every application role
    /// (issue #24, ADR-0013, G3 G4-24-01; the Done-when). One transaction on the owner connection, so a
    /// re-run never has a window with the writer's grant missing, and the order narrows first:
    /// <c>REVOKE ALL</c> from PUBLIC and from every module role on the schema, its tables and its
    /// sequences (which also clears <c>MAINTAIN</c>, <c>TRUNCATE</c>, column grants and grant options),
    /// then <c>GRANT USAGE</c> on the schema and <c>GRANT INSERT</c> on the table, by name and never
    /// <c>ALL</c>, to the writer roles only. A re-run converges to the same ACL and can never widen it.
    /// The generic <see cref="ProvisionModuleRoleAsync"/> is deliberately not used: it grants
    /// <c>UPDATE</c> and <c>DELETE</c>. Every identifier is a module constant, quoted with
    /// <see cref="QuoteIdentifier"/>.
    /// </summary>
    private static async Task ProvisionAppendOnlyGrantsAsync(
        string ownerConnectionString,
        string schema,
        string table,
        string[] writerRoles,
        string[] moduleRoles,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var quotedSchema = QuoteIdentifier(schema);
        var quotedTable = $"{quotedSchema}.{QuoteIdentifier(table)}";

        var statements = new List<string>
        {
            $"REVOKE ALL ON SCHEMA {quotedSchema} FROM PUBLIC",
            $"REVOKE ALL ON ALL TABLES IN SCHEMA {quotedSchema} FROM PUBLIC",
            $"REVOKE ALL ON ALL SEQUENCES IN SCHEMA {quotedSchema} FROM PUBLIC",
        };

        foreach (var role in moduleRoles)
        {
            var quotedRole = QuoteIdentifier(role);
            statements.Add($"REVOKE ALL ON ALL TABLES IN SCHEMA {quotedSchema} FROM {quotedRole}");
            statements.Add($"REVOKE ALL ON ALL SEQUENCES IN SCHEMA {quotedSchema} FROM {quotedRole}");
            statements.Add($"REVOKE ALL ON SCHEMA {quotedSchema} FROM {quotedRole}");
        }

        foreach (var role in writerRoles)
        {
            var quotedRole = QuoteIdentifier(role);
            statements.Add($"GRANT USAGE ON SCHEMA {quotedSchema} TO {quotedRole}");
            statements.Add($"GRANT INSERT ON {quotedTable} TO {quotedRole}");
        }

        foreach (var statement in statements)
        {
            await ExecuteAsync(connection, statement, cancellationToken, transaction).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
    private static async Task ExecuteAsync(
        NpgsqlConnection connection, string sql, CancellationToken cancellationToken, NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
#pragma warning restore CA2100
}
