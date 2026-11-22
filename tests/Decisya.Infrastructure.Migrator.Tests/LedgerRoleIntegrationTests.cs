using System.Globalization;
using System.Security.Cryptography;
using Decisya.Modules.Entitlements;
using Decisya.Modules.Ledger;
using Decisya.Modules.Tenancy;
using Decisya.TestInfrastructure;
using Npgsql;

namespace Decisya.Infrastructure.Migrator.Tests;

/// <summary>
/// Issue #341, G1 Story 8 (NFR-84), G2 R-L17 and G3 M341-04 / M341-03 (c), against a real Postgres 18 database
/// migrated by <c>MigrationRunner.RunAsync</c>. The <c>decisya_ledger</c> role holds exactly SELECT, INSERT and
/// UPDATE on the ledger tables (never DELETE, TRUNCATE or any DDL), nothing in other modules' schemas, and,
/// as an audit writer under ADR-0013 amendment 1, exactly USAGE on <c>audit</c> and INSERT on
/// <c>audit.audit_records</c>. The grants are asserted as exact ACLs, and are the same after a second run, and
/// after a run that finds them widened (the revoke-then-grant step converges and never widens). These sit beside
/// the other role tests because they need the migrator's own role provisioning and no module host.
/// </summary>
[Trait("Category", "Integration")]
[Collection(MigratorClusterCollectionDefinition.Name)]
public sealed class LedgerRoleIntegrationTests(PostgresFixture pg)
{
    private const string AuditTable = "audit.audit_records";

    private static readonly string[] LedgerTables = ["ledger.accounts", "ledger.account_books"];

    private static readonly string[] TablePrivileges =
        ["SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER", "MAINTAIN"];

    private static string NewPassword() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));

    private static string BuildConnectionString(string ownerConnectionString, string role, string password) =>
        new NpgsqlConnectionStringBuilder(ownerConnectionString) { Username = role, Password = password }.ConnectionString;

    private sealed record Provisioned(string Owner, string TenancyPassword, string EntitlementsPassword, string LedgerPassword)
    {
        public string AsLedger => BuildConnectionString(Owner, LedgerModule.DatabaseRole, LedgerPassword);

        public string AsTenancy => BuildConnectionString(Owner, TenancyModule.DatabaseRole, TenancyPassword);

        public string AsEntitlements => BuildConnectionString(Owner, EntitlementsModule.DatabaseRole, EntitlementsPassword);
    }

    private async Task<Provisioned> ProvisionAsync(CancellationToken cancellationToken)
    {
        var owner = await pg.CreateEmptyDatabaseAsync(cancellationToken);
        var provisioned = new Provisioned(owner, NewPassword(), NewPassword(), NewPassword());
        await RunAsync(provisioned, cancellationToken);
        return provisioned;
    }

    private static Task RunAsync(Provisioned p, CancellationToken cancellationToken) =>
        MigrationRunner.RunAsync(p.Owner, p.TenancyPassword, p.EntitlementsPassword, p.LedgerPassword, cancellationToken);

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

#pragma warning disable CA2100 // every call site passes a fixed literal or a string built from module constants.
    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<string>> RowsAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        var rows = new List<string>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? "NULL");
        }

        return rows;
    }
#pragma warning restore CA2100

    private static async Task<long> CountAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken) =>
        Convert.ToInt64(await ScalarAsync(connection, sql, cancellationToken), CultureInfo.InvariantCulture);

    private static async Task AssertInsufficientPrivilegeAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        var act = () => ExecuteAsync(connection, sql, cancellationToken);
        var assertion = await act.Should().ThrowAsync<PostgresException>(sql);
        assertion.Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, sql);
    }

    /// <summary>
    /// Every ACL entry that is not the object owner's own, over the ledger tables and history, the audit table and
    /// history, and the two schemas, as "object|grantee|privilege|grantable" lines (PUBLIC as "PUBLIC"), plus a
    /// count of column-level grants. The whole privilege picture the issue touches, in one comparable value.
    /// </summary>
    private static async Task<List<string>> AclSnapshotAsync(NpgsqlConnection owner, CancellationToken cancellationToken)
    {
        var lines = await RowsAsync(
            owner,
            "SELECT c.oid::regclass::text || '|' || CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee) END " +
            "|| '|' || a.privilege_type || '|' || a.is_grantable::text " +
            "FROM pg_class c, aclexplode(c.relacl) a " +
            "WHERE c.oid IN ('ledger.accounts'::regclass, 'ledger.account_books'::regclass, 'ledger.\"__EFMigrationsHistory\"'::regclass, " +
            "'audit.audit_records'::regclass, 'audit.\"__EFMigrationsHistory\"'::regclass) AND a.grantee <> c.relowner",
            cancellationToken);

        lines.AddRange(await RowsAsync(
            owner,
            "SELECT 'schema ' || n.nspname || '|' || CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee) END " +
            "|| '|' || a.privilege_type || '|' || a.is_grantable::text " +
            "FROM pg_namespace n, aclexplode(n.nspacl) a WHERE n.nspname IN ('ledger', 'audit') AND a.grantee <> n.nspowner",
            cancellationToken));

        lines.Add("column grants|" + await CountAsync(
            owner,
            "SELECT count(*) FROM pg_attribute WHERE attacl IS NOT NULL AND attrelid IN ('ledger.accounts'::regclass, 'ledger.account_books'::regclass, 'audit.audit_records'::regclass)",
            cancellationToken).ConfigureAwait(false));

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static List<string> ExpectedAclSnapshot()
    {
        var expected = new List<string>
        {
            $"audit.audit_records|{EntitlementsModule.DatabaseRole}|INSERT|false",
            $"audit.audit_records|{LedgerModule.DatabaseRole}|INSERT|false",
            $"schema audit|{EntitlementsModule.DatabaseRole}|USAGE|false",
            $"schema audit|{LedgerModule.DatabaseRole}|USAGE|false",
            $"schema ledger|{LedgerModule.DatabaseRole}|USAGE|false",
            "column grants|0",
        };

        foreach (var table in LedgerTables)
        {
            foreach (var privilege in new[] { "SELECT", "INSERT", "UPDATE" })
            {
                expected.Add($"{table}|{LedgerModule.DatabaseRole}|{privilege}|false");
            }
        }

        return expected;
    }

    [Fact]
    public async Task Decisya_ledger_holds_exactly_SELECT_INSERT_and_UPDATE_on_the_ledger_tables_and_the_same_after_a_second_RunAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provisioned = await ProvisionAsync(cancellationToken);

        foreach (var pass in new[] { 1, 2 })
        {
            if (pass == 2)
            {
                await RunAsync(provisioned, cancellationToken);
            }

            await using var owner = await OpenAsync(provisioned.Owner, cancellationToken);

            // The exact ACL of everything the issue touches: nothing more, nothing for PUBLIC, no column grants.
            var snapshot = await AclSnapshotAsync(owner, cancellationToken);
            snapshot.Should().BeEquivalentTo(
                ExpectedAclSnapshot(), $"pass {pass}: decisya_ledger's whole ACL, exactly (no DELETE, no history table, nothing for PUBLIC)");

            foreach (var table in LedgerTables)
            {
                foreach (var privilege in TablePrivileges)
                {
                    (await ScalarAsync(owner, $"SELECT has_table_privilege('{LedgerModule.DatabaseRole}', '{table}', '{privilege}')", cancellationToken))
                        .Should().Be(privilege is "SELECT" or "INSERT" or "UPDATE", $"pass {pass}: {LedgerModule.DatabaseRole} {privilege} on {table}");
                }
            }

            (await ScalarAsync(owner, $"SELECT has_schema_privilege('{LedgerModule.DatabaseRole}', 'ledger', 'USAGE')", cancellationToken)).Should().Be(true);
            (await ScalarAsync(owner, $"SELECT has_schema_privilege('{LedgerModule.DatabaseRole}', 'ledger', 'CREATE')", cancellationToken)).Should().Be(false);
            (await ScalarAsync(owner, $"SELECT has_table_privilege('{LedgerModule.DatabaseRole}', 'ledger.\"__EFMigrationsHistory\"', 'SELECT')", cancellationToken)).Should().Be(false);
        }
    }

    [Fact]
    public async Task Decisya_ledger_can_select_insert_and_update_but_cannot_delete_truncate_or_run_any_DDL()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provisioned = await ProvisionAsync(cancellationToken);

        await using var connection = await OpenAsync(provisioned.AsLedger, cancellationToken);
        (await ScalarAsync(connection, "SELECT current_user", cancellationToken)).Should().Be(LedgerModule.DatabaseRole);

        var tenantId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO ledger.account_books (id, tenant_id, lock_version, created_at) VALUES (@id, @tenant, 0, now())", connection))
        {
            insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("tenant", tenantId);
            (await insert.ExecuteNonQueryAsync(cancellationToken)).Should().Be(1);
        }

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO ledger.accounts (id, tenant_id, name, currency, opening_balance_minor, opening_date, created_at) " +
            "VALUES (@id, @tenant, 'Everyday', 'EUR', 125000, '2026-11-01', now())", connection))
        {
            insert.Parameters.AddWithValue("id", accountId);
            insert.Parameters.AddWithValue("tenant", tenantId);
            (await insert.ExecuteNonQueryAsync(cancellationToken)).Should().Be(1);
        }

        foreach (var sql in new[]
        {
            "SELECT count(*) FROM ledger.accounts WHERE tenant_id = @tenant",
            "SELECT count(*) FROM ledger.account_books WHERE tenant_id = @tenant",
        })
        {
#pragma warning disable CA2100 // fixed literals from the array above.
            await using var select = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
            select.Parameters.AddWithValue("tenant", tenantId);
            (await select.ExecuteScalarAsync(cancellationToken)).Should().Be(1L);
        }

        // The lock statement of the create transaction and the rename/currency write both need UPDATE.
        foreach (var sql in new[]
        {
            "UPDATE ledger.account_books SET lock_version = lock_version + 1 WHERE tenant_id = @tenant",
            "UPDATE ledger.accounts SET name = 'Renamed' WHERE tenant_id = @tenant",
        })
        {
#pragma warning disable CA2100 // fixed literals from the array above.
            await using var update = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
            update.Parameters.AddWithValue("tenant", tenantId);
            (await update.ExecuteNonQueryAsync(cancellationToken)).Should().Be(1);
        }

        string[] forbiddenStatements =
        [
            "DELETE FROM ledger.accounts",
            "DELETE FROM ledger.account_books",
            "TRUNCATE ledger.accounts",
            "TRUNCATE ledger.account_books",
            "CREATE TABLE public.probe_table (id int)",
            "CREATE TEMP TABLE probe_temp (id int)",
            "CREATE TABLE ledger.probe_table (id int)",
            "ALTER TABLE ledger.accounts ADD COLUMN probe int",
            "DROP TABLE ledger.accounts",
            "DROP TABLE ledger.account_books",
            "CREATE SCHEMA probe_schema",
            "CREATE INDEX probe_index ON ledger.accounts (name)",
            "SET ROLE postgres",
        ];

        foreach (var statement in forbiddenStatements)
        {
            await AssertInsufficientPrivilegeAsync(connection, statement, cancellationToken);
        }

        var readHistory = () => ScalarAsync(connection, "SELECT count(*) FROM ledger.\"__EFMigrationsHistory\"", cancellationToken);
        var historyAssertion = await readHistory.Should().ThrowAsync<PostgresException>();
        historyAssertion.Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);

        // The forbidden statements changed nothing.
        await using var owner = await OpenAsync(provisioned.Owner, cancellationToken);
        (await CountAsync(owner, $"SELECT count(*) FROM ledger.accounts WHERE id = '{accountId}' AND name = 'Renamed'", cancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task Decisya_ledger_is_an_audit_writer_with_exactly_USAGE_on_the_schema_and_INSERT_on_the_table()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provisioned = await ProvisionAsync(cancellationToken);

        foreach (var pass in new[] { 1, 2 })
        {
            if (pass == 2)
            {
                await RunAsync(provisioned, cancellationToken);
            }

            await using var owner = await OpenAsync(provisioned.Owner, cancellationToken);

            foreach (var privilege in TablePrivileges)
            {
                (await ScalarAsync(owner, $"SELECT has_table_privilege('{LedgerModule.DatabaseRole}', '{AuditTable}', '{privilege}')", cancellationToken))
                    .Should().Be(privilege == "INSERT", $"pass {pass}: {LedgerModule.DatabaseRole} {privilege} on {AuditTable}");
            }

            (await ScalarAsync(owner, $"SELECT has_schema_privilege('{LedgerModule.DatabaseRole}', 'audit', 'USAGE')", cancellationToken)).Should().Be(true);
            (await ScalarAsync(owner, $"SELECT has_schema_privilege('{LedgerModule.DatabaseRole}', 'audit', 'CREATE')", cancellationToken)).Should().Be(false);
            (await ScalarAsync(owner, $"SELECT has_table_privilege('{LedgerModule.DatabaseRole}', 'audit.\"__EFMigrationsHistory\"', 'SELECT')", cancellationToken)).Should().Be(false);

            // Entitlements stays a writer and Tenancy stays out, unchanged by the new writer.
            (await ScalarAsync(owner, $"SELECT has_table_privilege('{EntitlementsModule.DatabaseRole}', '{AuditTable}', 'INSERT')", cancellationToken)).Should().Be(true);
            (await ScalarAsync(owner, $"SELECT has_schema_privilege('{TenancyModule.DatabaseRole}', 'audit', 'USAGE')", cancellationToken)).Should().Be(false);
        }

        await using var connection = await OpenAsync(provisioned.AsLedger, cancellationToken);

        // INSERT works with no RETURNING and no SELECT (all the EF append needs), for each of the three ledger actions.
        foreach (var action in new[] { "ledger.account.create", "ledger.account.rename", "ledger.account.currency_change" })
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO audit.audit_records (id, tenant_id, occurred_at, actor_user_id, action, outcome, feature_key, trace_id, subject_id) " +
                "VALUES (@id, @tenant, now(), '3f2d1c9a-8b7e-4d6c-a5f4-0e1d2c3b4a59', @action, 'succeeded', NULL, NULL, @subject)",
                connection);
            insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("tenant", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("action", action);
            insert.Parameters.AddWithValue("subject", Guid.CreateVersion7());
            (await insert.ExecuteNonQueryAsync(cancellationToken)).Should().Be(1);
        }

        string[] forbidden =
        [
            "SELECT count(*) FROM audit.audit_records",
            "UPDATE audit.audit_records SET action = 'ledger.account.rename'",
            "DELETE FROM audit.audit_records",
            "TRUNCATE audit.audit_records",
            "SELECT count(*) FROM audit.\"__EFMigrationsHistory\"",
            "ALTER TABLE audit.audit_records ADD COLUMN probe int",
            "DROP TABLE audit.audit_records",
            "CREATE TABLE audit.t (id int)",
        ];

        foreach (var statement in forbidden)
        {
            await AssertInsufficientPrivilegeAsync(connection, statement, cancellationToken);
        }

        await using var ownerAfter = await OpenAsync(provisioned.Owner, cancellationToken);
        (await CountAsync(ownerAfter, $"SELECT count(*) FROM {AuditTable}", cancellationToken)).Should().Be(3, "only the three inserts changed the table");
    }

    /// <summary>ADR-0005 cross-schema test, every direction a third module role adds.</summary>
    [Fact]
    public async Task Decisya_ledger_cannot_read_or_write_the_tenancy_or_entitlements_schema_and_they_cannot_reach_ledger()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provisioned = await ProvisionAsync(cancellationToken);

        await using (var ledger = await OpenAsync(provisioned.AsLedger, cancellationToken))
        {
            await AssertInsufficientPrivilegeAsync(ledger, "SELECT count(*) FROM tenancy.tenants", cancellationToken);
            await AssertInsufficientPrivilegeAsync(ledger, "SELECT count(*) FROM tenancy.memberships", cancellationToken);
            await AssertInsufficientPrivilegeAsync(ledger, "SELECT count(*) FROM entitlements.trial_grants", cancellationToken);
            await AssertInsufficientPrivilegeAsync(ledger, "SELECT count(*) FROM entitlements.feature_overrides", cancellationToken);
            await AssertInsufficientPrivilegeAsync(
                ledger, "INSERT INTO tenancy.tenants (tenant_id, created_at) VALUES (gen_random_uuid(), now())", cancellationToken);
            await AssertInsufficientPrivilegeAsync(
                ledger,
                "INSERT INTO entitlements.trial_grants (id, tenant_id, plan, starts_at, ends_at) VALUES (gen_random_uuid(), gen_random_uuid(), 'Pro', now(), now() + interval '1 day')",
                cancellationToken);

            foreach (var schema in new[] { "tenancy", "entitlements" })
            {
                (await ScalarAsync(ledger, $"SELECT has_schema_privilege(current_user, '{schema}', 'USAGE')", cancellationToken)).Should().Be(false, schema);
                (await ScalarAsync(ledger, $"SELECT has_schema_privilege(current_user, '{schema}', 'CREATE')", cancellationToken)).Should().Be(false, schema);
            }

            (await ScalarAsync(ledger, "SELECT has_schema_privilege(current_user, 'ledger', 'USAGE')", cancellationToken)).Should().Be(true);
        }

        foreach (var (role, connectionString) in new[]
        {
            (TenancyModule.DatabaseRole, provisioned.AsTenancy),
            (EntitlementsModule.DatabaseRole, provisioned.AsEntitlements),
        })
        {
            await using var connection = await OpenAsync(connectionString, cancellationToken);
            await AssertInsufficientPrivilegeAsync(connection, "SELECT count(*) FROM ledger.accounts", cancellationToken);
            await AssertInsufficientPrivilegeAsync(connection, "SELECT count(*) FROM ledger.account_books", cancellationToken);
            await AssertInsufficientPrivilegeAsync(
                connection,
                "INSERT INTO ledger.account_books (id, tenant_id, lock_version, created_at) VALUES (gen_random_uuid(), gen_random_uuid(), 0, now())",
                cancellationToken);
            (await ScalarAsync(connection, "SELECT has_schema_privilege(current_user, 'ledger', 'USAGE')", cancellationToken)).Should().Be(false, role);
            (await ScalarAsync(connection, "SELECT has_schema_privilege(current_user, 'ledger', 'CREATE')", cancellationToken)).Should().Be(false, role);
        }
    }

    [Fact]
    public async Task Decisya_ledger_carries_no_elevated_attribute_no_role_membership_no_MAINTAIN_and_its_own_SCRAM_verifier()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provisioned = await ProvisionAsync(cancellationToken);

        await using var owner = await OpenAsync(provisioned.Owner, cancellationToken);

        await using (var rolesCommand = new NpgsqlCommand(
            "SELECT rolsuper, rolcreatedb, rolcreaterole, rolreplication, rolbypassrls, rolinherit FROM pg_roles WHERE rolname = @role", owner))
        {
            rolesCommand.Parameters.AddWithValue("role", LedgerModule.DatabaseRole);
            await using var reader = await rolesCommand.ExecuteReaderAsync(cancellationToken);
            (await reader.ReadAsync(cancellationToken)).Should().BeTrue();

            for (var i = 0; i < reader.FieldCount; i++)
            {
                reader.GetBoolean(i).Should().BeFalse($"pg_roles column {reader.GetName(i)} should be false for {LedgerModule.DatabaseRole}");
            }
        }

        (await CountAsync(
            owner,
            $"SELECT count(*) FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.member WHERE r.rolname = '{LedgerModule.DatabaseRole}'",
            cancellationToken)).Should().Be(0);
        (await ScalarAsync(owner, $"SELECT pg_has_role('{LedgerModule.DatabaseRole}', 'pg_maintain', 'MEMBER')", cancellationToken)).Should().Be(false);

        // The role owns nothing, so it can neither DROP nor ALTER what it uses.
        (await CountAsync(
            owner,
            $"SELECT count(*) FROM pg_class c JOIN pg_roles r ON r.oid = c.relowner WHERE r.rolname = '{LedgerModule.DatabaseRole}'",
            cancellationToken)).Should().Be(0);

        var stored = (string)(await ScalarAsync(
            owner, $"SELECT passwd FROM pg_shadow WHERE usename = '{LedgerModule.DatabaseRole}'", cancellationToken))!;
        stored.Should().StartWith("SCRAM-SHA-256$");
        stored.Should().NotContain(provisioned.LedgerPassword);

        // It authenticates with its own password and with no other module's.
        await using var authenticated = new NpgsqlConnection(provisioned.AsLedger);
        var act = () => authenticated.OpenAsync(cancellationToken);
        await act.Should().NotThrowAsync();

        await using var wrong = new NpgsqlConnection(BuildConnectionString(provisioned.Owner, LedgerModule.DatabaseRole, provisioned.EntitlementsPassword));
        var wrongAct = () => wrong.OpenAsync(cancellationToken);
        var failure = await wrongAct.Should().ThrowAsync<PostgresException>();
        failure.Which.SqlState.Should().Be(PostgresErrorCodes.InvalidPassword);
    }

    [Fact]
    public async Task A_run_that_finds_the_ledger_grants_widened_narrows_them_again_and_never_widens_anything()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provisioned = await ProvisionAsync(cancellationToken);

        await using (var owner = await OpenAsync(provisioned.Owner, cancellationToken))
        {
            // Everything a wider earlier provisioning, or a hand edit, could have left behind.
            string[] widen =
            [
                $"GRANT DELETE, TRUNCATE, REFERENCES, TRIGGER ON ALL TABLES IN SCHEMA ledger TO {LedgerModule.DatabaseRole} WITH GRANT OPTION",
                $"GRANT ALL ON ledger.\"__EFMigrationsHistory\" TO {LedgerModule.DatabaseRole}",
                $"GRANT UPDATE (name) ON ledger.accounts TO {LedgerModule.DatabaseRole}",
                $"GRANT UPDATE, DELETE, SELECT ON {AuditTable} TO {LedgerModule.DatabaseRole}",
            ];

            foreach (var statement in widen)
            {
                await ExecuteAsync(owner, statement, cancellationToken);
            }

            // Sanity: the pre-seed really is wider, so the narrowing below proves something.
            (await ScalarAsync(owner, $"SELECT has_table_privilege('{LedgerModule.DatabaseRole}', 'ledger.accounts', 'DELETE')", cancellationToken)).Should().Be(true);
            (await ScalarAsync(owner, $"SELECT has_table_privilege('{LedgerModule.DatabaseRole}', '{AuditTable}', 'UPDATE')", cancellationToken)).Should().Be(true);
            (await AclSnapshotAsync(owner, cancellationToken)).Should().NotBeEquivalentTo(ExpectedAclSnapshot());
        }

        await RunAsync(provisioned, cancellationToken);

        await using var ownerAfter = await OpenAsync(provisioned.Owner, cancellationToken);

        // The revoke-then-grant step (ledger) and the append-only step (audit) converge to the exact expected ACL:
        // the whole snapshot is again what a first run gives, column grants included.
        (await AclSnapshotAsync(ownerAfter, cancellationToken)).Should().BeEquivalentTo(ExpectedAclSnapshot());

        await using var ledger = await OpenAsync(provisioned.AsLedger, cancellationToken);
        await AssertInsufficientPrivilegeAsync(ledger, "DELETE FROM ledger.accounts", cancellationToken);
        await AssertInsufficientPrivilegeAsync(ledger, "TRUNCATE ledger.accounts", cancellationToken);
        await AssertInsufficientPrivilegeAsync(ledger, "UPDATE audit.audit_records SET action = 'ledger.account.rename'", cancellationToken);
        await AssertInsufficientPrivilegeAsync(ledger, "CREATE TABLE ledger.probe_table (id int)", cancellationToken);
    }
}
