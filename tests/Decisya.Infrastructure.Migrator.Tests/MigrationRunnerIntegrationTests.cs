using System.Security.Cryptography;
using Decisya.Modules.Tenancy;
using Decisya.TestInfrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Decisya.Infrastructure.Migrator.Tests;

/// <summary>
/// G3 G4-21-05, against a real Postgres 18 database (<see cref="PostgresFixture.CreateEmptyDatabaseAsync"/>):
/// idempotence, the <c>decisya_tenancy</c> role's DML-only privileges, and the SCRAM-SHA-256
/// verifier's round trip.
/// </summary>
[Trait("Category", "Integration")]
[Collection(MigratorClusterCollectionDefinition.Name)]
public sealed class MigrationRunnerIntegrationTests(PostgresFixture pg)
{
    private static string NewPassword() =>
        // 40 lowercase hex characters: alphanumeric, well over the 32-character minimum.
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));

    private static string BuildTenancyConnectionString(string ownerConnectionString, string password) =>
        new NpgsqlConnectionStringBuilder(ownerConnectionString)
        {
            Username = TenancyModule.DatabaseRole,
            Password = password,
        }.ConnectionString;

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
#pragma warning disable CA2100 // every call site below passes a fixed literal or a parameterized command; see ExecuteAsync's own note.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
#pragma warning disable CA2100 // every call site below passes a fixed, literal DDL/DML statement — never request or caller input.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [Fact]
    public async Task RunAsync_is_idempotent_when_run_twice_against_the_same_database()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ownerConnectionString = await pg.CreateEmptyDatabaseAsync(cancellationToken);
        var password = NewPassword();

        await MigrationRunner.RunAsync(ownerConnectionString, password, NewPassword(), cancellationToken);
        var act = () => MigrationRunner.RunAsync(ownerConnectionString, password, NewPassword(), cancellationToken);

        await act.Should().NotThrowAsync();

        await using var tenancyConnection = new NpgsqlConnection(BuildTenancyConnectionString(ownerConnectionString, password));
        await tenancyConnection.OpenAsync(cancellationToken);
        (await ScalarAsync(tenancyConnection, "SELECT current_user", cancellationToken)).Should().Be(TenancyModule.DatabaseRole);
    }

    /// <summary>G3 red test: "and once against a second database in the same container while the role already exists": <c>decisya_tenancy</c> is cluster-wide, so <c>CREATE ROLE</c> must tolerate <c>duplicate_object</c>.</summary>
    [Fact]
    public async Task RunAsync_tolerates_a_second_database_in_the_same_cluster_where_the_role_already_exists()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var firstOwnerConnectionString = await pg.CreateEmptyDatabaseAsync(cancellationToken);
        var password = NewPassword();
        await MigrationRunner.RunAsync(firstOwnerConnectionString, password, NewPassword(), cancellationToken);

        var secondOwnerConnectionString = await pg.CreateEmptyDatabaseAsync(cancellationToken);
        var act = () => MigrationRunner.RunAsync(secondOwnerConnectionString, password, NewPassword(), cancellationToken);

        await act.Should().NotThrowAsync("decisya_tenancy is cluster-wide; CREATE ROLE must catch duplicate_object");

        await using var tenancyConnection = new NpgsqlConnection(BuildTenancyConnectionString(secondOwnerConnectionString, password));
        await tenancyConnection.OpenAsync(cancellationToken);
        (await ScalarAsync(tenancyConnection, "SELECT current_user", cancellationToken)).Should().Be(TenancyModule.DatabaseRole);
    }

    /// <summary>The best possible "known vector" proof: Postgres itself accepts the client-computed verifier for the exact plaintext password it was derived from, and the value it persists is a SCRAM-SHA-256 verifier, never the plaintext.</summary>
    [Fact]
    public async Task The_computed_SCRAM_verifier_authenticates_with_the_original_plaintext_password_and_is_stored_in_verifier_form()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ownerConnectionString = await pg.CreateEmptyDatabaseAsync(cancellationToken);
        var password = NewPassword();

        await MigrationRunner.RunAsync(ownerConnectionString, password, NewPassword(), cancellationToken);

        await using var authenticated = new NpgsqlConnection(BuildTenancyConnectionString(ownerConnectionString, password));
        var act = () => authenticated.OpenAsync(cancellationToken);
        await act.Should().NotThrowAsync("the SCRAM verifier must authenticate with the exact plaintext password it was computed from");

        await using var ownerConnection = new NpgsqlConnection(ownerConnectionString);
        await ownerConnection.OpenAsync(cancellationToken);
        var stored = (string)(await ScalarAsync(
            ownerConnection, $"SELECT passwd FROM pg_shadow WHERE usename = '{TenancyModule.DatabaseRole}'", cancellationToken))!;

        stored.Should().StartWith("SCRAM-SHA-256$");
        stored.Should().NotContain(password);
    }

    [Fact]
    public async Task Decisya_tenancy_role_has_no_DDL_privilege_and_cannot_read_the_migrations_history_table()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ownerConnectionString = await pg.CreateEmptyDatabaseAsync(cancellationToken);
        var password = NewPassword();
        await MigrationRunner.RunAsync(ownerConnectionString, password, NewPassword(), cancellationToken);

        await using var connection = new NpgsqlConnection(BuildTenancyConnectionString(ownerConnectionString, password));
        await connection.OpenAsync(cancellationToken);

        (await ScalarAsync(connection, "SELECT current_user", cancellationToken)).Should().Be(TenancyModule.DatabaseRole);

        string[] forbiddenStatements =
        [
            "CREATE TABLE tenancy.probe_table (id int)",
            "CREATE TEMP TABLE probe_temp (id int)",
            "ALTER TABLE tenancy.tenants ADD COLUMN probe int",
            "DROP TABLE tenancy.tenants",
            "TRUNCATE tenancy.memberships",
            "CREATE SCHEMA probe_schema",
            "SET ROLE postgres",
        ];

        foreach (var statement in forbiddenStatements)
        {
            var act = () => ExecuteAsync(connection, statement, cancellationToken);
            var assertion = await act.Should().ThrowAsync<PostgresException>(statement);
            assertion.Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, statement);
        }

        var readHistory = () => ScalarAsync(connection, "SELECT count(*) FROM tenancy.\"__EFMigrationsHistory\"", cancellationToken);
        var historyAssertion = await readHistory.Should().ThrowAsync<PostgresException>();
        historyAssertion.Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Decisya_tenancy_role_can_read_write_update_and_delete_its_own_tables()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ownerConnectionString = await pg.CreateEmptyDatabaseAsync(cancellationToken);
        var password = NewPassword();
        await MigrationRunner.RunAsync(ownerConnectionString, password, NewPassword(), cancellationToken);

        await using var connection = new NpgsqlConnection(BuildTenancyConnectionString(ownerConnectionString, password));
        await connection.OpenAsync(cancellationToken);

        var tenantId = Guid.CreateVersion7();

        await using (var insert = new NpgsqlCommand("INSERT INTO tenancy.tenants (tenant_id, created_at) VALUES (@id, now())", connection))
        {
            insert.Parameters.AddWithValue("id", tenantId);
            (await insert.ExecuteNonQueryAsync(cancellationToken)).Should().Be(1);
        }

        await using (var select = new NpgsqlCommand("SELECT count(*) FROM tenancy.tenants WHERE tenant_id = @id", connection))
        {
            select.Parameters.AddWithValue("id", tenantId);
            (await select.ExecuteScalarAsync(cancellationToken)).Should().Be(1L);
        }

        await using (var update = new NpgsqlCommand("UPDATE tenancy.tenants SET created_at = now() WHERE tenant_id = @id", connection))
        {
            update.Parameters.AddWithValue("id", tenantId);
            (await update.ExecuteNonQueryAsync(cancellationToken)).Should().Be(1);
        }

        await using (var delete = new NpgsqlCommand("DELETE FROM tenancy.tenants WHERE tenant_id = @id", connection))
        {
            delete.Parameters.AddWithValue("id", tenantId);
            (await delete.ExecuteNonQueryAsync(cancellationToken)).Should().Be(1);
        }
    }

    [Fact]
    public async Task Decisya_tenancy_role_carries_no_elevated_attribute_and_no_role_membership()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ownerConnectionString = await pg.CreateEmptyDatabaseAsync(cancellationToken);
        var password = NewPassword();
        await MigrationRunner.RunAsync(ownerConnectionString, password, NewPassword(), cancellationToken);

        await using var ownerConnection = new NpgsqlConnection(ownerConnectionString);
        await ownerConnection.OpenAsync(cancellationToken);

        await using (var rolesCommand = new NpgsqlCommand(
            "SELECT rolsuper, rolcreatedb, rolcreaterole, rolreplication, rolbypassrls FROM pg_roles WHERE rolname = @role", ownerConnection))
        {
            rolesCommand.Parameters.AddWithValue("role", TenancyModule.DatabaseRole);
            await using var reader = await rolesCommand.ExecuteReaderAsync(cancellationToken);
            (await reader.ReadAsync(cancellationToken)).Should().BeTrue();

            for (var i = 0; i < reader.FieldCount; i++)
            {
                reader.GetBoolean(i).Should().BeFalse($"pg_roles column {reader.GetName(i)} should be false for {TenancyModule.DatabaseRole}");
            }
        }

        var memberOfCount = await ScalarAsync(
            ownerConnection,
            $"SELECT count(*) FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.member WHERE r.rolname = '{TenancyModule.DatabaseRole}'",
            cancellationToken);
        Convert.ToInt64(memberOfCount, System.Globalization.CultureInfo.InvariantCulture).Should().Be(0);
    }

    /// <summary>G3 red test: "connecting to the postgres maintenance database ... is refused or returns no readable table."</summary>
    [Fact]
    public async Task Decisya_tenancy_role_reads_no_table_in_the_postgres_maintenance_database()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ownerConnectionString = await pg.CreateEmptyDatabaseAsync(cancellationToken);
        var password = NewPassword();
        await MigrationRunner.RunAsync(ownerConnectionString, password, NewPassword(), cancellationToken);

        var maintenanceConnectionString = new NpgsqlConnectionStringBuilder(BuildTenancyConnectionString(ownerConnectionString, password))
        {
            Database = "postgres",
        }.ConnectionString;

        await using var connection = new NpgsqlConnection(maintenanceConnectionString);

        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            return; // Refused outright also satisfies the requirement.
        }

        var readableTables = await ScalarAsync(
            connection,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema')",
            cancellationToken);
        Convert.ToInt64(readableTables, System.Globalization.CultureInfo.InvariantCulture).Should().Be(
            0, "decisya_tenancy has no grant on any table outside its own schema");
    }

    [Fact]
    public async Task A_successful_run_logs_only_a_fixed_completion_message_never_the_password_or_connection_string()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(provider);
            builder.SetMinimumLevel(LogLevel.Trace);
        });
        var logger = loggerFactory.CreateLogger("Decisya.Infrastructure.Migrator");

        var ownerConnectionString = await pg.CreateEmptyDatabaseAsync(cancellationToken);
        var password = NewPassword();
        var entitlementsPassword = NewPassword();

        await MigrationRunner.RunAsync(ownerConnectionString, password, entitlementsPassword, cancellationToken);
        MigratorLog.MigrationCompleted(logger); // exactly Program.cs's own success path.

        var record = provider.Records.Should().ContainSingle().Which;
        record.Contains(password).Should().BeFalse();
        record.Contains(entitlementsPassword).Should().BeFalse();
        record.Contains(ownerConnectionString).Should().BeFalse();
        record.Contains("Password=").Should().BeFalse();
        record.Message.Should().Be("Tenancy module migration and role provisioning completed.");
    }
}
