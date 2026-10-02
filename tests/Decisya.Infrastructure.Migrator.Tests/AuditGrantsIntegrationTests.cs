using System.Globalization;
using System.Security.Cryptography;
using Decisya.Modules.Audit;
using Decisya.Modules.Entitlements;
using Decisya.Modules.Tenancy;
using Decisya.TestInfrastructure;
using Npgsql;

namespace Decisya.Infrastructure.Migrator.Tests;

/// <summary>
/// G3 G4-24-01, NFR-36 and the Done-when of issue #24 ("audit rows cannot be updated or deleted at
/// DB role level"), against a real Postgres 18 database migrated by <c>MigrationRunner.RunAsync</c>.
/// The ACL is asserted exactly (owner plus <c>decisya_entitlements</c> with INSERT and no grant
/// option, never "at least"), column ACLs are null, and everything holds again after a second run.
/// Postgres raises WARNING 01007 rather than 42501 for a <c>GRANT</c> by a role that holds some
/// privilege on the object without grant option (G2 reconciliation), so that statement is asserted
/// as "42501 or no exception", followed by the unchanged ACL.
/// </summary>
[Trait("Category", "Integration")]
[Collection(MigratorClusterCollectionDefinition.Name)]
public sealed class AuditGrantsIntegrationTests(PostgresFixture pg)
{
    private const string AuditTable = "audit.audit_records";

    private static readonly string[] TablePrivileges =
        ["SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER", "MAINTAIN"];

    private static string NewPassword() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));

    private static string BuildConnectionString(string ownerConnectionString, string role, string password) =>
        new NpgsqlConnectionStringBuilder(ownerConnectionString) { Username = role, Password = password }.ConnectionString;

    private sealed record Provisioned(string Owner, string TenancyPassword, string EntitlementsPassword)
    {
        public string AsEntitlements => BuildConnectionString(Owner, EntitlementsModule.DatabaseRole, EntitlementsPassword);

        public string AsTenancy => BuildConnectionString(Owner, TenancyModule.DatabaseRole, TenancyPassword);
    }

    private async Task<Provisioned> ProvisionAsync(CancellationToken cancellationToken)
    {
        var owner = await pg.CreateEmptyDatabaseAsync(cancellationToken);
        var provisioned = new Provisioned(owner, NewPassword(), NewPassword());
        await RunAsync(provisioned, cancellationToken);
        return provisioned;
    }

    private static Task RunAsync(Provisioned p, CancellationToken cancellationToken) =>
        MigrationRunner.RunAsync(p.Owner, p.TenancyPassword, p.EntitlementsPassword, cancellationToken);

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
            var fields = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                fields.Add(Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "NULL");
            }

            rows.Add(string.Join("|", fields));
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

    private static async Task InsertRecordAsync(NpgsqlConnection connection, Guid tenant, CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand(
            "INSERT INTO audit.audit_records (id, tenant_id, occurred_at, actor_user_id, action, outcome, feature_key, trace_id) " +
            "VALUES (@id, @tenant, now(), '3f2d1c9a-8b7e-4d6c-a5f4-0e1d2c3b4a59', 'entitlements.trial.start', 'succeeded', NULL, NULL)",
            connection);
        insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
        insert.Parameters.AddWithValue("tenant", tenant);
        (await insert.ExecuteNonQueryAsync(cancellationToken)).Should().Be(1);
    }

    /// <summary>The ACL of a relation as sorted "grantee|privilege|grantable" lines, PUBLIC as "PUBLIC", owner included.</summary>
    private static Task<List<string>> TableAclAsync(NpgsqlConnection owner, string relation, CancellationToken cancellationToken) =>
        RowsAsync(
            owner,
            "SELECT CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee) END AS grantee, a.privilege_type, a.is_grantable " +
            $"FROM pg_class c, aclexplode(c.relacl) a WHERE c.oid = '{relation}'::regclass ORDER BY 1, 2",
            cancellationToken);

    private static Task<List<string>> SchemaAclAsync(NpgsqlConnection owner, CancellationToken cancellationToken) =>
        RowsAsync(
            owner,
            "SELECT CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee) END AS grantee, a.privilege_type, a.is_grantable " +
            "FROM pg_namespace n, aclexplode(n.nspacl) a WHERE n.nspname = 'audit' ORDER BY 1, 2",
            cancellationToken);

    private static async Task<string> OwnerNameAsync(NpgsqlConnection owner, CancellationToken cancellationToken) =>
        (string)(await ScalarAsync(owner, $"SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = '{AuditTable}'::regclass", cancellationToken))!;

    /// <summary>
    /// The G4-24-01 structural proof, read as the owner: the exact table and schema ACL, null column
    /// ACLs, nothing for PUBLIC or <c>decisya_tenancy</c>, no sequence, function or <c>decisya_audit</c> role,
    /// and the <c>has_*_privilege</c> matrix for both roles. Run after the first and again after the second <c>RunAsync</c>.
    /// </summary>
    private static async Task AssertAppendOnlyStructureAsync(string ownerConnectionString, CancellationToken cancellationToken)
    {
        await using var owner = await OpenAsync(ownerConnectionString, cancellationToken);
        var ownerName = await OwnerNameAsync(owner, cancellationToken);

        // Table ACL: the owner's own entries, plus decisya_entitlements INSERT with no grant option. Nothing else.
        var acl = await TableAclAsync(owner, AuditTable, cancellationToken);
        var foreign = acl.Where(line => !line.StartsWith(ownerName + "|", StringComparison.Ordinal)).ToList();
        foreign.Should().Equal([$"{EntitlementsModule.DatabaseRole}|INSERT|False"], "the table ACL is exactly the owner plus INSERT for the writer");
        acl.Where(line => line.StartsWith(ownerName + "|", StringComparison.Ordinal)).Should().NotBeEmpty("the owner keeps its own privileges");

        // Column ACLs: no column-level grant for anyone.
        (await CountAsync(
            owner,
            $"SELECT count(*) FROM pg_attribute WHERE attrelid = '{AuditTable}'::regclass AND attnum > 0 AND NOT attisdropped AND attacl IS NOT NULL",
            cancellationToken)).Should().Be(0);

        // Schema ACL: the owner plus USAGE for decisya_entitlements.
        var schemaAcl = await SchemaAclAsync(owner, cancellationToken);
        schemaAcl.Where(line => !line.StartsWith(ownerName + "|", StringComparison.Ordinal)).Should()
            .Equal([$"{EntitlementsModule.DatabaseRole}|USAGE|False"], "the schema ACL is exactly the owner plus USAGE for the writer");

        // The history table: nothing for anyone but the owner.
        var historyAcl = await RowsAsync(
            owner,
            "SELECT CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee) END FROM pg_class c, aclexplode(c.relacl) a " +
            "WHERE c.oid = 'audit.\"__EFMigrationsHistory\"'::regclass",
            cancellationToken);
        historyAcl.Should().OnlyContain(grantee => grantee == ownerName);

        // PUBLIC: nothing on the schema, the table or its columns.
        acl.Should().NotContain(line => line.StartsWith("PUBLIC|", StringComparison.Ordinal));
        schemaAcl.Should().NotContain(line => line.StartsWith("PUBLIC|", StringComparison.Ordinal));

        // The decisya_entitlements has_*_privilege matrix: INSERT only, among the eight table privileges.
        foreach (var privilege in TablePrivileges)
        {
            var held = (bool)(await ScalarAsync(
                owner, $"SELECT has_table_privilege('{EntitlementsModule.DatabaseRole}', '{AuditTable}', '{privilege}')", cancellationToken))!;
            held.Should().Be(privilege == "INSERT", $"{EntitlementsModule.DatabaseRole} {privilege}");

            (await ScalarAsync(
                owner, $"SELECT has_table_privilege('{TenancyModule.DatabaseRole}', '{AuditTable}', '{privilege}')", cancellationToken))
                .Should().Be(false, $"{TenancyModule.DatabaseRole} {privilege}");
        }

        (await ScalarAsync(owner, $"SELECT has_schema_privilege('{EntitlementsModule.DatabaseRole}', 'audit', 'USAGE')", cancellationToken)).Should().Be(true);
        (await ScalarAsync(owner, $"SELECT has_schema_privilege('{EntitlementsModule.DatabaseRole}', 'audit', 'CREATE')", cancellationToken)).Should().Be(false);
        (await ScalarAsync(owner, $"SELECT has_schema_privilege('{TenancyModule.DatabaseRole}', 'audit', 'USAGE')", cancellationToken)).Should().Be(false);
        (await ScalarAsync(owner, $"SELECT has_schema_privilege('{TenancyModule.DatabaseRole}', 'audit', 'CREATE')", cancellationToken)).Should().Be(false);

        // No sequence, no function, no decisya_audit role.
        (await CountAsync(owner, "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'audit' AND c.relkind = 'S'", cancellationToken)).Should().Be(0);
        (await CountAsync(owner, "SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'audit'", cancellationToken)).Should().Be(0);
        (await CountAsync(owner, "SELECT count(*) FROM pg_roles WHERE rolname = 'decisya_audit'", cancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task The_audit_table_ACL_is_exactly_the_owner_plus_INSERT_for_decisya_entitlements_and_the_same_after_a_second_RunAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provisioned = await ProvisionAsync(cancellationToken);

        await AssertAppendOnlyStructureAsync(provisioned.Owner, cancellationToken);

        await RunAsync(provisioned, cancellationToken);

        await AssertAppendOnlyStructureAsync(provisioned.Owner, cancellationToken);
    }

    [Fact]
    public async Task Pre_seeded_UPDATE_DELETE_TRUNCATE_column_PUBLIC_and_tenancy_grants_are_narrowed_by_RunAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provisioned = await ProvisionAsync(cancellationToken);

        await using (var owner = await OpenAsync(provisioned.Owner, cancellationToken))
        {
            // Everything a wider earlier provisioning, or a hand edit, could have left behind.
            string[] widen =
            [
                $"GRANT UPDATE, DELETE, TRUNCATE, SELECT ON {AuditTable} TO {EntitlementsModule.DatabaseRole} WITH GRANT OPTION",
                $"GRANT UPDATE (action) ON {AuditTable} TO {EntitlementsModule.DatabaseRole}",
                $"GRANT CREATE ON SCHEMA audit TO {EntitlementsModule.DatabaseRole}",
                $"GRANT ALL ON audit.\"__EFMigrationsHistory\" TO {EntitlementsModule.DatabaseRole}",
                $"GRANT USAGE ON SCHEMA audit TO {TenancyModule.DatabaseRole}",
                $"GRANT ALL ON {AuditTable} TO {TenancyModule.DatabaseRole}",
                $"GRANT SELECT ON {AuditTable} TO PUBLIC",
                "GRANT USAGE ON SCHEMA audit TO PUBLIC",
            ];

            foreach (var statement in widen)
            {
                await ExecuteAsync(owner, statement, cancellationToken);
            }

            // Sanity: the pre-seed really is wider, so the narrowing below proves something.
            (await ScalarAsync(owner, $"SELECT has_table_privilege('{EntitlementsModule.DatabaseRole}', '{AuditTable}', 'UPDATE')", cancellationToken)).Should().Be(true);
            (await ScalarAsync(owner, $"SELECT has_table_privilege('{EntitlementsModule.DatabaseRole}', '{AuditTable}', 'DELETE')", cancellationToken)).Should().Be(true);
            (await ScalarAsync(owner, $"SELECT has_table_privilege('{TenancyModule.DatabaseRole}', '{AuditTable}', 'SELECT')", cancellationToken)).Should().Be(true);
            await InsertRecordAsync(owner, Guid.CreateVersion7(), cancellationToken);
        }

        await RunAsync(provisioned, cancellationToken);

        await AssertAppendOnlyStructureAsync(provisioned.Owner, cancellationToken);

        await using var entitlements = await OpenAsync(provisioned.AsEntitlements, cancellationToken);
        await AssertInsufficientPrivilegeAsync(entitlements, "UPDATE audit.audit_records SET action = 'x'", cancellationToken);
        await AssertInsufficientPrivilegeAsync(entitlements, "DELETE FROM audit.audit_records", cancellationToken);
        await AssertInsufficientPrivilegeAsync(entitlements, "TRUNCATE audit.audit_records", cancellationToken);
        await AssertInsufficientPrivilegeAsync(entitlements, "SELECT count(*) FROM audit.audit_records", cancellationToken);
        await AssertInsufficientPrivilegeAsync(entitlements, "CREATE TABLE audit.t (id int)", cancellationToken);

        await using var tenancy = await OpenAsync(provisioned.AsTenancy, cancellationToken);
        await AssertInsufficientPrivilegeAsync(tenancy, "SELECT count(*) FROM audit.audit_records", cancellationToken);
    }

    [Fact]
    public async Task Decisya_entitlements_can_INSERT_but_UPDATE_DELETE_TRUNCATE_and_SELECT_fail_with_42501_and_the_seeded_row_is_unchanged()
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
            var tenant = Guid.CreateVersion7();
            await InsertRecordAsync(owner, tenant, cancellationToken);
            var before = await RowsAsync(owner, "SELECT md5(t::text) FROM audit.audit_records t ORDER BY id", cancellationToken);

            await using var connection = await OpenAsync(provisioned.AsEntitlements, cancellationToken);
            (await ScalarAsync(connection, "SELECT current_user", cancellationToken)).Should().Be(EntitlementsModule.DatabaseRole);

            // INSERT works, with no RETURNING and no SELECT: that is all EF's append needs.
            await InsertRecordAsync(connection, tenant, cancellationToken);

            string[] forbidden =
            [
                "UPDATE audit.audit_records SET action = 'x'",
                "UPDATE audit.audit_records SET actor_user_id = 'someone-else' WHERE true",
                "DELETE FROM audit.audit_records",
                "TRUNCATE audit.audit_records",
                "TRUNCATE audit.audit_records, entitlements.trial_grants",
                "SELECT count(*) FROM audit.audit_records",
                "SELECT id FROM audit.audit_records",
                "SELECT count(*) FROM audit.\"__EFMigrationsHistory\"",
                "ALTER TABLE audit.audit_records ADD COLUMN probe int",
                "DROP TABLE audit.audit_records",
                "CREATE TABLE audit.t (id int)",
                "CREATE TRIGGER probe BEFORE INSERT ON audit.audit_records FOR EACH ROW EXECUTE FUNCTION pg_catalog.suppress_redundant_updates_trigger()",
                "SET ROLE postgres",
            ];

            foreach (var statement in forbidden)
            {
                await AssertInsufficientPrivilegeAsync(connection, statement, cancellationToken);
            }

            // LOCK TABLE needs a transaction block; ACCESS EXCLUSIVE needs UPDATE, DELETE or TRUNCATE, which INSERT does not imply.
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                await AssertInsufficientPrivilegeAsync(connection, "LOCK TABLE audit.audit_records IN ACCESS EXCLUSIVE MODE", cancellationToken);
                await transaction.RollbackAsync(cancellationToken);
            }

            var after = await RowsAsync(owner, "SELECT md5(t::text) FROM audit.audit_records t ORDER BY id", cancellationToken);
            after.Should().HaveCount(before.Count + 1, "only the one INSERT changed the table");
            after.Should().Contain(before, "every row that existed before is byte-identical afterwards");
        }
    }

    [Fact]
    public async Task Decisya_tenancy_has_no_privilege_on_the_audit_schema_and_every_statement_fails_with_42501()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provisioned = await ProvisionAsync(cancellationToken);

        await using (var seed = await OpenAsync(provisioned.Owner, cancellationToken))
        {
            await InsertRecordAsync(seed, Guid.CreateVersion7(), cancellationToken);
        }

        await RunAsync(provisioned, cancellationToken);

        await using var connection = await OpenAsync(provisioned.AsTenancy, cancellationToken);
        (await ScalarAsync(connection, "SELECT current_user", cancellationToken)).Should().Be(TenancyModule.DatabaseRole);

        string[] forbidden =
        [
            "SELECT count(*) FROM audit.audit_records",
            "INSERT INTO audit.audit_records (id, tenant_id, occurred_at, actor_user_id, action, outcome) VALUES (gen_random_uuid(), gen_random_uuid(), now(), 'x', 'entitlements.trial.start', 'succeeded')",
            "UPDATE audit.audit_records SET action = 'x'",
            "DELETE FROM audit.audit_records",
            "TRUNCATE audit.audit_records",
            "SELECT count(*) FROM audit.\"__EFMigrationsHistory\"",
            "GRANT UPDATE ON audit.audit_records TO PUBLIC",
            "CREATE TABLE audit.t (id int)",
        ];

        foreach (var statement in forbidden)
        {
            await AssertInsufficientPrivilegeAsync(connection, statement, cancellationToken);
        }

        // Looking the table up by name needs USAGE on the schema, so the privilege matrix is read as the owner.
        await using var owner = await OpenAsync(provisioned.Owner, cancellationToken);
        (await ScalarAsync(owner, $"SELECT has_schema_privilege('{TenancyModule.DatabaseRole}', 'audit', 'USAGE')", cancellationToken)).Should().Be(false);
        (await ScalarAsync(owner, $"SELECT has_schema_privilege('{TenancyModule.DatabaseRole}', 'audit', 'CREATE')", cancellationToken)).Should().Be(false);
        foreach (var privilege in TablePrivileges)
        {
            (await ScalarAsync(owner, $"SELECT has_table_privilege('{TenancyModule.DatabaseRole}', '{AuditTable}', '{privilege}')", cancellationToken))
                .Should().Be(false, privilege);
        }
    }

    [Fact]
    public async Task PUBLIC_holds_nothing_on_the_audit_schema_its_table_its_columns_or_its_functions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provisioned = await ProvisionAsync(cancellationToken);
        await RunAsync(provisioned, cancellationToken);

        await using var owner = await OpenAsync(provisioned.Owner, cancellationToken);

        (await TableAclAsync(owner, AuditTable, cancellationToken)).Should().NotContain(line => line.StartsWith("PUBLIC|", StringComparison.Ordinal));
        (await SchemaAclAsync(owner, cancellationToken)).Should().NotContain(line => line.StartsWith("PUBLIC|", StringComparison.Ordinal));
        (await CountAsync(
            owner,
            $"SELECT count(*) FROM pg_attribute a, aclexplode(a.attacl) x WHERE a.attrelid = '{AuditTable}'::regclass AND x.grantee = 0",
            cancellationToken)).Should().Be(0);

        // PUBLIC's one remaining Postgres default is EXECUTE on functions, and the schema has none.
        (await CountAsync(owner, "SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'audit'", cancellationToken)).Should().Be(0);

        // A role with no privilege of its own (a fresh one, member of nothing) reaches nothing through PUBLIC.
        var probe = $"probe_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}";
        await ExecuteAsync(owner, $"CREATE ROLE {probe}", cancellationToken);
        try
        {
            (await ScalarAsync(owner, $"SELECT has_schema_privilege('{probe}', 'audit', 'USAGE')", cancellationToken)).Should().Be(false);
            foreach (var privilege in TablePrivileges)
            {
                (await ScalarAsync(owner, $"SELECT has_table_privilege('{probe}', '{AuditTable}', '{privilege}')", cancellationToken))
                    .Should().Be(false, privilege);
            }
        }
        finally
        {
            await ExecuteAsync(owner, $"DROP ROLE {probe}", cancellationToken);
        }
    }

    [Fact]
    public async Task Neither_module_role_holds_MAINTAIN_or_membership_of_pg_maintain_or_any_other_role()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provisioned = await ProvisionAsync(cancellationToken);
        await RunAsync(provisioned, cancellationToken);

        await using var owner = await OpenAsync(provisioned.Owner, cancellationToken);

        foreach (var role in new[] { EntitlementsModule.DatabaseRole, TenancyModule.DatabaseRole })
        {
            (await ScalarAsync(owner, $"SELECT has_table_privilege('{role}', '{AuditTable}', 'MAINTAIN')", cancellationToken)).Should().Be(false, role);
            (await ScalarAsync(owner, $"SELECT pg_has_role('{role}', 'pg_maintain', 'MEMBER')", cancellationToken)).Should().Be(false, role);
            (await CountAsync(
                owner,
                $"SELECT count(*) FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.member WHERE r.rolname = '{role}'",
                cancellationToken)).Should().Be(0, role);
        }

        await using var entitlements = await OpenAsync(provisioned.AsEntitlements, cancellationToken);
        // VACUUM and ANALYZE only warn and skip for a role without MAINTAIN (no exception), so they are not asserted here.
        await AssertInsufficientPrivilegeAsync(entitlements, "REINDEX TABLE audit.audit_records", cancellationToken);
        await AssertInsufficientPrivilegeAsync(entitlements, "CLUSTER audit.audit_records", cancellationToken);
    }

    [Fact]
    public async Task GRANT_UPDATE_TO_PUBLIC_as_decisya_entitlements_is_42501_or_a_notice_without_exception_and_the_ACL_is_unchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provisioned = await ProvisionAsync(cancellationToken);

        await using var owner = await OpenAsync(provisioned.Owner, cancellationToken);
        var aclBefore = await TableAclAsync(owner, AuditTable, cancellationToken);
        var columnsBefore = await CountAsync(
            owner, $"SELECT count(*) FROM pg_attribute WHERE attrelid = '{AuditTable}'::regclass AND attacl IS NOT NULL", cancellationToken);

        foreach (var statement in new[]
        {
            $"GRANT UPDATE ON {AuditTable} TO PUBLIC",
            $"GRANT DELETE, TRUNCATE ON {AuditTable} TO {EntitlementsModule.DatabaseRole}",
            $"GRANT UPDATE ON {AuditTable} TO {TenancyModule.DatabaseRole}",
        })
        {
            await using var connection = await OpenAsync(provisioned.AsEntitlements, cancellationToken);
            var notices = new List<string>();
            connection.Notice += (_, e) => notices.Add(e.Notice.SqlState);

            // Postgres raises WARNING 01007 (a notice) for a role that holds a privilege without grant option,
            // and 42501 where it holds none. The test never asserts "throws".
            string? failure = null;
            try
            {
                await ExecuteAsync(connection, statement, cancellationToken);
            }
            catch (PostgresException ex)
            {
                failure = ex.SqlState;
            }

            if (failure is not null)
            {
                failure.Should().Be(PostgresErrorCodes.InsufficientPrivilege, statement);
            }
            else
            {
                notices.Should().Contain("01007", $"{statement}: no exception means 'no privileges were granted'");
            }
        }

        (await TableAclAsync(owner, AuditTable, cancellationToken)).Should().Equal(aclBefore, "no GRANT by the application role may change the ACL");
        (await CountAsync(
            owner, $"SELECT count(*) FROM pg_attribute WHERE attrelid = '{AuditTable}'::regclass AND attacl IS NOT NULL", cancellationToken))
            .Should().Be(columnsBefore);

        // The behavioural half: UPDATE still fails for the writer afterwards.
        await using var after = await OpenAsync(provisioned.AsEntitlements, cancellationToken);
        await AssertInsufficientPrivilegeAsync(after, "UPDATE audit.audit_records SET action = 'x'", cancellationToken);

        // decisya_tenancy has no privilege at all, so its GRANT fails outright (no USAGE on the schema).
        await using var tenancy = await OpenAsync(provisioned.AsTenancy, cancellationToken);
        await AssertInsufficientPrivilegeAsync(tenancy, $"GRANT UPDATE ON {AuditTable} TO PUBLIC", cancellationToken);
    }

    [Fact]
    public async Task The_audit_migration_creates_exactly_audit_records_with_its_constraints_and_index_and_its_history_table_in_the_audit_schema()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provisioned = await ProvisionAsync(cancellationToken);

        await using var owner = await OpenAsync(provisioned.Owner, cancellationToken);

        (await RowsAsync(
            owner, "SELECT table_name FROM information_schema.tables WHERE table_schema = 'audit' ORDER BY table_name", cancellationToken))
            .Should().Equal("__EFMigrationsHistory", "audit_records");

        foreach (var name in new[]
        {
            "ck_audit_records_action", "ck_audit_records_outcome", "ck_audit_records_feature_key",
            "ck_audit_records_trace_id", "ck_audit_records_actor",
        })
        {
            (await CountAsync(owner, $"SELECT count(*) FROM pg_constraint WHERE conname = '{name}' AND conrelid = '{AuditTable}'::regclass", cancellationToken))
                .Should().Be(1, name);
        }

        (await CountAsync(
            owner, "SELECT count(*) FROM pg_indexes WHERE schemaname = 'audit' AND indexname = 'ix_audit_records_tenant_occurred'", cancellationToken))
            .Should().Be(1);

        // No decisya_audit role: ADR-0013 point 3 (the first reader adds a SELECT-only role).
        (await CountAsync(owner, "SELECT count(*) FROM pg_roles WHERE rolname = 'decisya_audit'", cancellationToken)).Should().Be(0);
        AuditModule.Schema.Should().Be("audit");
    }
}
