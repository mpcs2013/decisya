using System.Globalization;
using System.Security.Cryptography;
using Decisya.Modules.Entitlements;
using Decisya.Modules.Tenancy;
using Decisya.TestInfrastructure;
using Npgsql;

namespace Decisya.Infrastructure.Migrator.Tests;

/// <summary>
/// G3 G4-23-04 (issue #23), against a real Postgres 18 database: the <c>decisya_entitlements</c>
/// role is DML-only on its own schema, the two module roles cannot read each other's schema
/// (ADR-0005 cross-schema test, deferred in #21 until a second module existed), and the run is
/// idempotent with both roles.
/// </summary>
[Trait("Category", "Integration")]
[Collection(MigratorClusterCollectionDefinition.Name)]
public sealed class EntitlementsRoleIntegrationTests(PostgresFixture pg)
{
    private static string NewPassword() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));

    private static string BuildConnectionString(string ownerConnectionString, string role, string password) =>
        new NpgsqlConnectionStringBuilder(ownerConnectionString) { Username = role, Password = password }.ConnectionString;

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
#pragma warning disable CA2100 // every call site passes a fixed literal.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
#pragma warning disable CA2100 // every call site passes a fixed literal.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AssertInsufficientPrivilegeAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        var act = () => ExecuteAsync(connection, sql, cancellationToken);
        var assertion = await act.Should().ThrowAsync<PostgresException>(sql);
        assertion.Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, sql);
    }

    private async Task<(string Owner, string TenancyPassword, string EntitlementsPassword)> ProvisionAsync(CancellationToken cancellationToken)
    {
        var owner = await pg.CreateEmptyDatabaseAsync(cancellationToken);
        var tenancyPassword = NewPassword();
        var entitlementsPassword = NewPassword();
        await MigrationRunner.RunAsync(owner, tenancyPassword, entitlementsPassword, cancellationToken);
        return (owner, tenancyPassword, entitlementsPassword);
    }

    [Fact]
    public async Task Decisya_entitlements_role_has_no_DDL_privilege_and_cannot_read_the_migrations_history_table()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (owner, _, password) = await ProvisionAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(BuildConnectionString(owner, EntitlementsModule.DatabaseRole, password));
        await connection.OpenAsync(cancellationToken);
        (await ScalarAsync(connection, "SELECT current_user", cancellationToken)).Should().Be(EntitlementsModule.DatabaseRole);

        string[] forbiddenStatements =
        [
            "CREATE TABLE entitlements.probe_table (id int)",
            "CREATE TEMP TABLE probe_temp (id int)",
            "ALTER TABLE entitlements.trial_grants ADD COLUMN probe int",
            "DROP TABLE entitlements.trial_grants",
            "DROP TABLE entitlements.feature_overrides",
            "TRUNCATE entitlements.feature_overrides",
            "CREATE SCHEMA probe_schema",
            "SET ROLE postgres",
        ];

        foreach (var statement in forbiddenStatements)
        {
            await AssertInsufficientPrivilegeAsync(connection, statement, cancellationToken);
        }

        var readHistory = () => ScalarAsync(connection, "SELECT count(*) FROM entitlements.\"__EFMigrationsHistory\"", cancellationToken);
        var historyAssertion = await readHistory.Should().ThrowAsync<PostgresException>();
        historyAssertion.Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Decisya_entitlements_role_can_read_write_update_and_delete_both_of_its_tables()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (owner, _, password) = await ProvisionAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(BuildConnectionString(owner, EntitlementsModule.DatabaseRole, password));
        await connection.OpenAsync(cancellationToken);

        var tenantId = Guid.CreateVersion7();

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO entitlements.trial_grants (id, tenant_id, plan, starts_at, ends_at) VALUES (@id, @tenant, 'Pro', now(), now() + interval '14 days')", connection))
        {
            insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("tenant", tenantId);
            (await insert.ExecuteNonQueryAsync(cancellationToken)).Should().Be(1);
        }

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO entitlements.feature_overrides (id, tenant_id, feature_key, reason, granted_at, expires_at) VALUES (@id, @tenant, 'forecasting.scenarios', 'pilot', now(), NULL)", connection))
        {
            insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("tenant", tenantId);
            (await insert.ExecuteNonQueryAsync(cancellationToken)).Should().Be(1);
        }

        foreach (var sql in new[]
        {
            "SELECT count(*) FROM entitlements.trial_grants WHERE tenant_id = @tenant",
            "SELECT count(*) FROM entitlements.feature_overrides WHERE tenant_id = @tenant",
        })
        {
#pragma warning disable CA2100 // fixed literals from the array above.
            await using var select = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
            select.Parameters.AddWithValue("tenant", tenantId);
            (await select.ExecuteScalarAsync(cancellationToken)).Should().Be(1L);
        }

        await using (var update = new NpgsqlCommand("UPDATE entitlements.trial_grants SET ends_at = ends_at + interval '1 day' WHERE tenant_id = @tenant", connection))
        {
            update.Parameters.AddWithValue("tenant", tenantId);
            (await update.ExecuteNonQueryAsync(cancellationToken)).Should().Be(1);
        }

        await using (var update = new NpgsqlCommand("UPDATE entitlements.feature_overrides SET reason = 'extended' WHERE tenant_id = @tenant", connection))
        {
            update.Parameters.AddWithValue("tenant", tenantId);
            (await update.ExecuteNonQueryAsync(cancellationToken)).Should().Be(1);
        }

        foreach (var sql in new[]
        {
            "DELETE FROM entitlements.trial_grants WHERE tenant_id = @tenant",
            "DELETE FROM entitlements.feature_overrides WHERE tenant_id = @tenant",
        })
        {
#pragma warning disable CA2100 // fixed literals from the array above.
            await using var delete = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
            delete.Parameters.AddWithValue("tenant", tenantId);
            (await delete.ExecuteNonQueryAsync(cancellationToken)).Should().Be(1);
        }
    }

    [Fact]
    public async Task Decisya_entitlements_role_carries_no_elevated_attribute_and_no_role_membership()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (owner, _, _) = await ProvisionAsync(cancellationToken);

        await using var ownerConnection = new NpgsqlConnection(owner);
        await ownerConnection.OpenAsync(cancellationToken);

        await using (var rolesCommand = new NpgsqlCommand(
            "SELECT rolsuper, rolcreatedb, rolcreaterole, rolreplication, rolbypassrls, rolinherit FROM pg_roles WHERE rolname = @role", ownerConnection))
        {
            rolesCommand.Parameters.AddWithValue("role", EntitlementsModule.DatabaseRole);
            await using var reader = await rolesCommand.ExecuteReaderAsync(cancellationToken);
            (await reader.ReadAsync(cancellationToken)).Should().BeTrue();

            // NOINHERIT is the last column: rolinherit must be false too.
            for (var i = 0; i < reader.FieldCount; i++)
            {
                reader.GetBoolean(i).Should().BeFalse($"pg_roles column {reader.GetName(i)} should be false for {EntitlementsModule.DatabaseRole}");
            }
        }

        var memberOf = await ScalarAsync(
            ownerConnection,
            $"SELECT count(*) FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.member WHERE r.rolname = '{EntitlementsModule.DatabaseRole}'",
            cancellationToken);
        Convert.ToInt64(memberOf, CultureInfo.InvariantCulture).Should().Be(0);
    }

    [Fact]
    public async Task The_entitlements_SCRAM_verifier_authenticates_and_is_stored_in_verifier_form()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (owner, _, password) = await ProvisionAsync(cancellationToken);

        await using var authenticated = new NpgsqlConnection(BuildConnectionString(owner, EntitlementsModule.DatabaseRole, password));
        var act = () => authenticated.OpenAsync(cancellationToken);
        await act.Should().NotThrowAsync();

        await using var ownerConnection = new NpgsqlConnection(owner);
        await ownerConnection.OpenAsync(cancellationToken);
        var stored = (string)(await ScalarAsync(
            ownerConnection, $"SELECT passwd FROM pg_shadow WHERE usename = '{EntitlementsModule.DatabaseRole}'", cancellationToken))!;
        stored.Should().StartWith("SCRAM-SHA-256$");
        stored.Should().NotContain(password);
    }

    [Fact]
    public async Task Each_module_role_authenticates_only_with_its_own_password()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (owner, tenancyPassword, entitlementsPassword) = await ProvisionAsync(cancellationToken);

        await using var wrongForEntitlements = new NpgsqlConnection(BuildConnectionString(owner, EntitlementsModule.DatabaseRole, tenancyPassword));
        var act1 = () => wrongForEntitlements.OpenAsync(cancellationToken);
        var failure1 = await act1.Should().ThrowAsync<PostgresException>();
        failure1.Which.SqlState.Should().Be(PostgresErrorCodes.InvalidPassword);

        await using var wrongForTenancy = new NpgsqlConnection(BuildConnectionString(owner, TenancyModule.DatabaseRole, entitlementsPassword));
        var act2 = () => wrongForTenancy.OpenAsync(cancellationToken);
        var failure2 = await act2.Should().ThrowAsync<PostgresException>();
        failure2.Which.SqlState.Should().Be(PostgresErrorCodes.InvalidPassword);
    }

    /// <summary>ADR-0005 cross-schema test, direction 1: <c>decisya_entitlements</c> cannot reach the tenancy schema.</summary>
    [Fact]
    public async Task Decisya_entitlements_role_cannot_read_or_write_the_tenancy_schema()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (owner, _, password) = await ProvisionAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(BuildConnectionString(owner, EntitlementsModule.DatabaseRole, password));
        await connection.OpenAsync(cancellationToken);

        await AssertInsufficientPrivilegeAsync(connection, "SELECT count(*) FROM tenancy.tenants", cancellationToken);
        await AssertInsufficientPrivilegeAsync(connection, "SELECT count(*) FROM tenancy.memberships", cancellationToken);
        await AssertInsufficientPrivilegeAsync(
            connection, "INSERT INTO tenancy.tenants (tenant_id, created_at) VALUES (gen_random_uuid(), now())", cancellationToken);

        (await ScalarAsync(connection, "SELECT has_schema_privilege(current_user, 'tenancy', 'USAGE')", cancellationToken)).Should().Be(false);
        (await ScalarAsync(connection, "SELECT has_schema_privilege(current_user, 'tenancy', 'CREATE')", cancellationToken)).Should().Be(false);
        (await ScalarAsync(connection, "SELECT has_schema_privilege(current_user, 'entitlements', 'USAGE')", cancellationToken)).Should().Be(true);

        // Issue #24 (ADR-0013): the one cross-schema write is INSERT on audit.audit_records, and nothing else in audit.
        (await ScalarAsync(connection, "SELECT has_schema_privilege(current_user, 'audit', 'USAGE')", cancellationToken)).Should().Be(true);
        (await ScalarAsync(connection, "SELECT has_schema_privilege(current_user, 'audit', 'CREATE')", cancellationToken)).Should().Be(false);
        await AssertInsufficientPrivilegeAsync(connection, "SELECT count(*) FROM audit.audit_records", cancellationToken);
        await AssertInsufficientPrivilegeAsync(connection, "UPDATE audit.audit_records SET action = 'x'", cancellationToken);
        await AssertInsufficientPrivilegeAsync(connection, "DELETE FROM audit.audit_records", cancellationToken);
    }

    /// <summary>ADR-0005 cross-schema test, direction 2: <c>decisya_tenancy</c> cannot reach the entitlements schema.</summary>
    [Fact]
    public async Task Decisya_tenancy_role_cannot_read_or_write_the_entitlements_schema()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (owner, tenancyPassword, _) = await ProvisionAsync(cancellationToken);

        await using var connection = new NpgsqlConnection(BuildConnectionString(owner, TenancyModule.DatabaseRole, tenancyPassword));
        await connection.OpenAsync(cancellationToken);

        await AssertInsufficientPrivilegeAsync(connection, "SELECT count(*) FROM entitlements.trial_grants", cancellationToken);
        await AssertInsufficientPrivilegeAsync(connection, "SELECT count(*) FROM entitlements.feature_overrides", cancellationToken);
        await AssertInsufficientPrivilegeAsync(
            connection,
            "INSERT INTO entitlements.trial_grants (id, tenant_id, plan, starts_at, ends_at) VALUES (gen_random_uuid(), gen_random_uuid(), 'Pro', now(), now() + interval '1 day')",
            cancellationToken);

        (await ScalarAsync(connection, "SELECT has_schema_privilege(current_user, 'entitlements', 'USAGE')", cancellationToken)).Should().Be(false);
        (await ScalarAsync(connection, "SELECT has_schema_privilege(current_user, 'entitlements', 'CREATE')", cancellationToken)).Should().Be(false);
        (await ScalarAsync(connection, "SELECT has_schema_privilege(current_user, 'tenancy', 'USAGE')", cancellationToken)).Should().Be(true);

        // Issue #24: decisya_tenancy has nothing at all in the audit schema.
        (await ScalarAsync(connection, "SELECT has_schema_privilege(current_user, 'audit', 'USAGE')", cancellationToken)).Should().Be(false);
        (await ScalarAsync(connection, "SELECT has_schema_privilege(current_user, 'audit', 'CREATE')", cancellationToken)).Should().Be(false);
        await AssertInsufficientPrivilegeAsync(connection, "SELECT count(*) FROM audit.audit_records", cancellationToken);
        await AssertInsufficientPrivilegeAsync(
            connection,
            "INSERT INTO audit.audit_records (id, tenant_id, occurred_at, actor_user_id, action, outcome) VALUES (gen_random_uuid(), gen_random_uuid(), now(), 'x', 'entitlements.trial.start', 'succeeded')",
            cancellationToken);
    }

    [Fact]
    public async Task RunAsync_is_idempotent_with_both_roles_and_the_privilege_tests_still_hold_afterwards()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var owner = await pg.CreateEmptyDatabaseAsync(cancellationToken);
        var tenancyPassword = NewPassword();
        var entitlementsPassword = NewPassword();

        await MigrationRunner.RunAsync(owner, tenancyPassword, entitlementsPassword, cancellationToken);
        var act = () => MigrationRunner.RunAsync(owner, tenancyPassword, entitlementsPassword, cancellationToken);
        await act.Should().NotThrowAsync();

        // A second database in the same cluster, where both roles already exist.
        var second = await pg.CreateEmptyDatabaseAsync(cancellationToken);
        var secondAct = () => MigrationRunner.RunAsync(second, tenancyPassword, entitlementsPassword, cancellationToken);
        await secondAct.Should().NotThrowAsync("both roles are cluster-wide; CREATE ROLE must tolerate duplicate_object for each");

        foreach (var database in new[] { owner, second })
        {
            await using var connection = new NpgsqlConnection(BuildConnectionString(database, EntitlementsModule.DatabaseRole, entitlementsPassword));
            await connection.OpenAsync(cancellationToken);
            await AssertInsufficientPrivilegeAsync(connection, "DROP TABLE entitlements.trial_grants", cancellationToken);
            await AssertInsufficientPrivilegeAsync(connection, "SELECT count(*) FROM tenancy.tenants", cancellationToken);
            (await ScalarAsync(connection, "SELECT count(*) FROM entitlements.trial_grants", cancellationToken)).Should().Be(0L);

            await using var tenancyConnection = new NpgsqlConnection(BuildConnectionString(database, TenancyModule.DatabaseRole, tenancyPassword));
            await tenancyConnection.OpenAsync(cancellationToken);
            await AssertInsufficientPrivilegeAsync(tenancyConnection, "SELECT count(*) FROM entitlements.trial_grants", cancellationToken);
            (await ScalarAsync(tenancyConnection, "SELECT count(*) FROM tenancy.tenants", cancellationToken)).Should().Be(0L);

            // Issue #24: the audit grants are the same in both databases after the re-run (AuditGrantsIntegrationTests asserts the exact ACL).
            await AssertInsufficientPrivilegeAsync(connection, "UPDATE audit.audit_records SET action = 'x'", cancellationToken);
            await AssertInsufficientPrivilegeAsync(connection, "DELETE FROM audit.audit_records", cancellationToken);
            await AssertInsufficientPrivilegeAsync(tenancyConnection, "SELECT count(*) FROM audit.audit_records", cancellationToken);
        }
    }

    [Fact]
    public async Task The_entitlements_migration_creates_both_tables_with_their_constraints_in_the_entitlements_schema_only()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (owner, _, _) = await ProvisionAsync(cancellationToken);

        await using var ownerConnection = new NpgsqlConnection(owner);
        await ownerConnection.OpenAsync(cancellationToken);

        var tables = new List<string>();
        await using (var command = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'entitlements' ORDER BY table_name", ownerConnection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add(reader.GetString(0));
            }
        }

        tables.Should().BeEquivalentTo(["__EFMigrationsHistory", "feature_overrides", "trial_grants"]);

        foreach (var name in new[] { "ux_trial_grants_tenant", "ux_feature_overrides_tenant_feature" })
        {
            (await ScalarAsync(ownerConnection, $"SELECT count(*) FROM pg_indexes WHERE schemaname = 'entitlements' AND indexname = '{name}'", cancellationToken))
                .Should().Be(1L, name);
        }

        foreach (var name in new[] { "ck_trial_grants_period", "ck_feature_overrides_expiry" })
        {
            (await ScalarAsync(ownerConnection, $"SELECT count(*) FROM pg_constraint WHERE conname = '{name}'", cancellationToken))
                .Should().Be(1L, name);
        }
    }
}
