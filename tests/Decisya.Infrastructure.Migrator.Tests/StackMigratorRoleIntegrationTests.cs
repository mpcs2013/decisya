using System.Globalization;
using System.Security.Cryptography;
using Decisya.AppHost;
using Decisya.Modules.Entitlements;
using Decisya.Modules.Tenancy;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Decisya.Infrastructure.Migrator.Tests;

/// <summary>
/// Issue #120, C-06 (flagged at G4): in the deployable stack the migrator is
/// <c>decisya_migrator</c>, created by <c>deploy/postgres/init/20-decisya-db.sh</c> as a
/// non-superuser that holds CREATEROLE and owns the <c>decisya</c> database.
/// <see cref="MigrationRunner"/> runs <c>ALTER ROLE ... NOREPLICATION NOBYPASSRLS</c> as that role,
/// which Postgres 16 and later may refuse to a non-superuser. This test provisions the migrator
/// exactly as the stack does (the real init script, in stack mode, in a throwaway container with
/// its own cluster, so no cluster-wide role leaks into the shared fixture) and runs the real
/// <see cref="MigrationRunner"/> as that role.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StackMigratorRoleIntegrationTests : IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(2);

    private IContainer? _container;

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static string NewSecret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    [Fact]
    public async Task The_non_superuser_stack_migrator_can_run_MigrationRunner_and_provision_the_module_roles()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var migratorPassword = NewSecret();
        var tenancyPassword = NewSecret();
        var entitlementsPassword = NewSecret();

        _container = BuildContainer(migratorPassword);
        await StartBoundedAsync(_container, cancellationToken);

        var migratorConnectionString = new NpgsqlConnectionStringBuilder
        {
            Host = _container.Hostname,
            Port = _container.GetMappedPublicPort(5432),
            Username = "decisya_migrator",
            Password = migratorPassword,
            Database = "decisya",
            Timeout = 30,
        }.ConnectionString;

        // Precondition: this really is the stack's migrator (not a superuser, CREATEROLE).
        await using (var connection = new NpgsqlConnection(migratorConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT rolsuper::text || ',' || rolcreaterole::text || ',' || rolreplication::text || ',' || rolbypassrls::text FROM pg_roles WHERE rolname = current_user",
                connection);
            (await command.ExecuteScalarAsync(cancellationToken)).Should().Be("false,true,false,false");
        }

        // The assertion under test: the migrator's role provisioning succeeds as this role.
        var act = () => MigrationRunner.RunAsync(
            migratorConnectionString, tenancyPassword, entitlementsPassword, cancellationToken);
        await act.Should().NotThrowAsync(
            "MigrationRunner must provision the module roles as the stack's non-superuser CREATEROLE migrator (C-06)");

        // The module roles exist, can log in with their own password and carry no elevated attribute.
        foreach (var (role, password) in new[]
        {
            (TenancyModule.DatabaseRole, tenancyPassword),
            (EntitlementsModule.DatabaseRole, entitlementsPassword),
        })
        {
            var roleConnectionString = new NpgsqlConnectionStringBuilder(migratorConnectionString)
            {
                Username = role,
                Password = password,
            }.ConnectionString;

            await using var connection = new NpgsqlConnection(roleConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                "SELECT rolsuper::text || ',' || rolcreatedb::text || ',' || rolcreaterole::text || ',' || rolreplication::text || ',' || rolbypassrls::text FROM pg_roles WHERE rolname = current_user",
                connection);
            Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture)
                .Should().Be("false,false,false,false,false", role);
        }

        // A re-run (the migrator runs on every start) is idempotent as the same role.
        var rerun = () => MigrationRunner.RunAsync(
            migratorConnectionString, tenancyPassword, entitlementsPassword, cancellationToken);
        await rerun.Should().NotThrowAsync("the stack's migrator runs on every start");
    }

    [Fact]
    public async Task PUBLIC_has_no_CONNECT_and_each_role_connects_only_to_its_own_database_in_the_stack()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var migratorPassword = NewSecret();
        _container = BuildContainer(migratorPassword);
        await StartBoundedAsync(_container, cancellationToken);

        var migratorConnectionString = new NpgsqlConnectionStringBuilder
        {
            Host = _container.Hostname,
            Port = _container.GetMappedPublicPort(5432),
            Username = "decisya_migrator",
            Password = migratorPassword,
            Database = "decisya",
            Timeout = 30,
        }.ConnectionString;

        await MigrationRunner.RunAsync(migratorConnectionString, NewSecret(), NewSecret(), cancellationToken);

        await using var connection = new NpgsqlConnection(migratorConnectionString);
        await connection.OpenAsync(cancellationToken);

        async Task<bool> CanConnectAsync(string who, string database)
        {
            await using var command = who == "public"
                ? new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM pg_database d, aclexplode(coalesce(d.datacl, acldefault('d', d.datdba))) a WHERE d.datname = @db AND a.grantee = 0 AND a.privilege_type = 'CONNECT')",
                    connection)
                : new NpgsqlCommand("SELECT has_database_privilege(@who, @db, 'CONNECT')", connection);
            command.Parameters.AddWithValue("db", database);
            if (who != "public")
            {
                command.Parameters.AddWithValue("who", who);
            }

            return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        foreach (var database in new[] { "postgres", "template1", "decisya", "keycloak" })
        {
            (await CanConnectAsync("public", database)).Should().BeFalse($"PUBLIC must not hold CONNECT on {database}");
        }

        foreach (var role in new[] { TenancyModule.DatabaseRole, EntitlementsModule.DatabaseRole })
        {
            (await CanConnectAsync(role, "decisya")).Should().BeTrue(role);
            (await CanConnectAsync(role, "keycloak")).Should().BeFalse($"{role} must not reach keycloak");
            (await CanConnectAsync(role, "postgres")).Should().BeFalse($"{role} must not reach postgres");
        }

        (await CanConnectAsync("keycloak", "keycloak")).Should().BeTrue();
        (await CanConnectAsync("keycloak", "decisya")).Should().BeFalse("keycloak's role must not reach decisya");
    }

    [Fact]
    public async Task A_pre_existing_module_role_with_an_elevated_attribute_fails_the_run_closed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var migratorPassword = NewSecret();
        var tenancyPassword = NewSecret();
        var entitlementsPassword = NewSecret();

        _container = BuildContainer(migratorPassword);
        await StartBoundedAsync(_container, cancellationToken);

        // As the container superuser (local socket, trust): a role created outside the migrator with CREATEDB,
        // and the admin grant the migrator needs so that its ALTER ROLE does not fail earlier with 42501.
        var role = TenancyModule.DatabaseRole;
        var setup = await _container.ExecAsync(
            [
                "psql", "-U", "postgres", "-d", "decisya", "-v", "ON_ERROR_STOP=1", "-c",
                $"CREATE ROLE \"{role}\" WITH LOGIN CREATEDB; GRANT \"{role}\" TO decisya_migrator WITH ADMIN OPTION;",
            ],
            cancellationToken);
        setup.ExitCode.Should().Be(0, setup.Stderr);

        var migratorConnectionString = new NpgsqlConnectionStringBuilder
        {
            Host = _container.Hostname,
            Port = _container.GetMappedPublicPort(5432),
            Username = "decisya_migrator",
            Password = migratorPassword,
            Database = "decisya",
            Timeout = 30,
        }.ConnectionString;

        var act = () => MigrationRunner.RunAsync(
            migratorConnectionString, tenancyPassword, entitlementsPassword, cancellationToken);

        var thrown = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        thrown.Message.Should().Contain(role).And.Contain("CREATEDB");
        thrown.Message.Should().NotContain(tenancyPassword).And.NotContain(migratorPassword);
    }

    private static IContainer BuildContainer(string migratorPassword)
    {
        var image = ContainerImages.Reference(
            ContainerImages.PostgresRegistry, ContainerImages.PostgresImage,
            ContainerImages.PostgresTag, ContainerImages.PostgresSha256);

        // Readable, not executable: as Aspire's WithInitFiles copies them, so the entrypoint
        // sources the scripts (the stack's `stackctl assemble` sets the bit; both paths must work).
        const uint NoExecBit = 0x1A4; // octal 0644

        var builder = new ContainerBuilder(image)
            .WithEnvironment("POSTGRES_PASSWORD", NewSecret())
            .WithEnvironment("DECISYA_KEYCLOAK_DB_PASSWORD", NewSecret())
            .WithEnvironment("DECISYA_STACK", "1")
            .WithEnvironment("DECISYA_MIGRATOR_DB_PASSWORD", migratorPassword)
            .WithPortBinding(5432, true);

        foreach (var script in Directory.GetFiles(RepoPaths.Find(Path.Combine("deploy", "postgres", "init")), "*.sh"))
        {
            builder = builder.WithResourceMapping(
                File.ReadAllBytes(script), "/docker-entrypoint-initdb.d/" + Path.GetFileName(script), NoExecBit);
        }

        // The init-time server listens on the socket only; TCP readiness means the final server is up.
        return builder
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted(
                "pg_isready", "-h", "127.0.0.1", "-U", "postgres"))
            .Build();
    }

    private static async Task StartBoundedAsync(IContainer container, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(StartTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            await container.StartAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Postgres with the stack init scripts did not accept a TCP connection within {StartTimeout} " +
                $"(container state: {container.State}).");
        }
    }
}
