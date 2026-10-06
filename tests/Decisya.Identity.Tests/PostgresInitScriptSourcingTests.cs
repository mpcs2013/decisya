using System.Text.RegularExpressions;
using Decisya.AppHost;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Decisya.Identity.Tests;

/// <summary>
/// Issue #120 regression (G4 fix): <c>deploy/postgres/init/20-decisya-db.sh</c> once ran
/// <c>exit 0</c> when <c>DECISYA_STACK</c> was unset. Aspire's <c>WithInitFiles</c> copies init
/// files without the exec bit, so the Postgres entrypoint SOURCES them, and that <c>exit</c> ended
/// the entrypoint: the container exited with code 0 right after init, Postgres never became
/// healthy, and every dependent resource (and the AppHost test suite) waited forever.
/// The static class below needs no Docker; <see cref="PostgresInitScriptSourcingTests"/> proves the
/// behaviour in a real container.
/// </summary>
public class PostgresInitScriptStaticTests
{
    // An exit with no status, or with status 0 / $?-less success, is a non-error exit.
    private static readonly Regex NonErrorExit = new(
        @"(^|[;&|{]|\bthen\b|\belse\b|\bdo\b)\s*exit(\s+0+)?\s*($|[;&|}])", RegexOptions.Compiled);

    private static readonly Regex TopLevelExit = new(@"^exit\b", RegexOptions.Compiled);

    private static string InitFolder { get; } = RepoPaths.Find(Path.Combine("deploy", "postgres", "init"));

    [Fact]
    public void The_init_folder_contains_the_scripts_this_guard_is_meant_to_cover()
    {
        // A guard that silently passes because it found nothing to guard is not a guard.
        var scripts = Directory.GetFiles(InitFolder, "*.sh").Select(Path.GetFileName).ToArray();

        scripts.Should().Contain("20-decisya-db.sh");
        scripts.Should().Contain(
            "10-keycloak-db.sh");
    }

    [Fact]
    public void No_init_script_exits_on_a_non_error_path_because_the_entrypoint_sources_it_without_the_exec_bit()
    {
        foreach (var script in Directory.GetFiles(InitFolder, "*.sh"))
        {
            foreach (var line in File.ReadAllLines(script).Where(l => !l.TrimStart().StartsWith('#')))
            {
                NonErrorExit.IsMatch(line).Should().BeFalse(
                    $"{Path.GetFileName(script)} must not end a sourcing shell on a success path: '{line}'");
            }
        }
    }

    [Fact]
    public void No_init_script_has_an_unindented_exit_at_all_because_a_sourced_exit_kills_the_entrypoint()
    {
        // An `exit 1` on an error path is acceptable inside a branch (it must stop the entrypoint
        // too); a column-0 exit is a top-level statement of the sourced file.
        foreach (var script in Directory.GetFiles(InitFolder, "*.sh"))
        {
            foreach (var line in File.ReadAllLines(script).Where(l => !l.TrimStart().StartsWith('#')))
            {
                TopLevelExit.IsMatch(line).Should().BeFalse(
                    $"{Path.GetFileName(script)} has a top-level exit: '{line}'");
            }
        }
    }

    [Fact]
    public void The_exit_detector_flags_the_old_script_shape()
    {
        NonErrorExit.IsMatch("    exit 0").Should().BeTrue();
        NonErrorExit.IsMatch("exit").Should().BeTrue();
        NonErrorExit.IsMatch("[ x ] && exit 0").Should().BeTrue();
        NonErrorExit.IsMatch("_f || { unset -f _f; exit 1; }").Should().BeFalse();
    }
}

/// <summary>
/// Copies <c>deploy/postgres/init</c> into <c>/docker-entrypoint-initdb.d</c> with NO exec bit
/// (as Aspire's <c>WithInitFiles</c> does), so the entrypoint sources the scripts, and asserts
/// the container is still running afterwards. Against the old script (a top-level
/// <c>exit 0</c> on the no-op path) the entrypoint shell ends right after init: the container
/// exits with code 0, never accepts a TCP connection, and the bounded start below throws.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresInitScriptSourcingTests : IAsyncDisposable
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

    [Fact]
    public async Task With_DECISYA_STACK_unset_the_sourced_init_scripts_leave_Postgres_running_and_connectable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        _container = BuildContainer(stackMode: false, migratorPassword: null);
        await StartBoundedAsync(_container, cancellationToken);

        _container.State.Should().Be(TestcontainersStates.Running);

        var select = await _container.ExecAsync(
            ["psql", "-h", "127.0.0.1", "-U", "postgres", "-tAc", "select 1"], cancellationToken);
        select.ExitCode.Should().Be(0);
        select.Stdout.Trim().Should().Be("1");

        // 10-keycloak-db.sh still ran (it has no exit path); 20-decisya-db.sh was a no-op.
        (await Query(_container, "select count(*) from pg_roles where rolname = 'keycloak'", cancellationToken))
            .Should().Be("1");
        (await Query(_container, "select count(*) from pg_roles where rolname = 'decisya_migrator'", cancellationToken))
            .Should().Be("0");
        (await Query(_container, "select count(*) from pg_database where datname = 'decisya'", cancellationToken))
            .Should().Be("0");
    }

    [Fact]
    public async Task With_DECISYA_STACK_set_the_sourced_init_script_creates_the_migrator_and_Postgres_stays_running()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        _container = BuildContainer(stackMode: true, migratorPassword: Canaries.SecretShaped(32));
        await StartBoundedAsync(_container, cancellationToken);

        _container.State.Should().Be(TestcontainersStates.Running);

        (await Query(
            _container,
            "select rolsuper::text || rolcreatedb::text || rolcreaterole::text from pg_roles where rolname = 'decisya_migrator'",
            cancellationToken)).Should().Be("falsefalsetrue");
        (await Query(
            _container,
            "select pg_get_userbyid(datdba) from pg_database where datname = 'decisya'",
            cancellationToken)).Should().Be("decisya_migrator");
    }

    [Fact]
    public async Task With_DECISYA_STACK_set_and_no_migrator_password_the_sourced_init_fails_closed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        _container = BuildContainer(stackMode: true, migratorPassword: null);

        // No wait strategy: the entrypoint must stop on its own, non-zero, at the `:?` guard.
        using var timeout = new CancellationTokenSource(StartTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        await _container.StartAsync(linked.Token);

        var exitCode = await WaitForExitAsync(_container, timeout, linked.Token);

        exitCode.Should().NotBe(0, "a stack init without the migrator password must never come up");
    }

    private static IContainer BuildContainer(bool stackMode, string? migratorPassword)
    {
        var image = ContainerImages.Reference(
            ContainerImages.PostgresRegistry, ContainerImages.PostgresImage,
            ContainerImages.PostgresTag, ContainerImages.PostgresSha256);

        // Generated at run time, never a literal, never printed.
        var postgresPassword = Canaries.SecretShaped(32);
        var keycloakPassword = Canaries.SecretShaped(32);

        // Readable, not executable: exactly what Aspire's WithInitFiles produces, so the
        // entrypoint sources the scripts instead of running them as child processes.
        const uint NoExecBit = 0x1A4; // octal 0644

        var builder = new ContainerBuilder(image)
            .WithEnvironment("POSTGRES_PASSWORD", postgresPassword)
            .WithEnvironment("PGPASSWORD", postgresPassword)
            .WithEnvironment("DECISYA_KEYCLOAK_DB_PASSWORD", keycloakPassword);

        foreach (var script in Directory.GetFiles(RepoPaths.Find(Path.Combine("deploy", "postgres", "init")), "*.sh"))
        {
            builder = builder.WithResourceMapping(
                File.ReadAllBytes(script), "/docker-entrypoint-initdb.d/" + Path.GetFileName(script), NoExecBit);
        }

        if (stackMode)
        {
            builder = builder.WithEnvironment("DECISYA_STACK", "1");
        }

        if (migratorPassword is not null)
        {
            builder = builder.WithEnvironment("DECISYA_MIGRATOR_DB_PASSWORD", migratorPassword);
        }

        // Fail-closed case has no readiness to wait for; the others wait on TCP readiness, which
        // only the final server offers (the init-time server listens on the socket alone).
        return stackMode && migratorPassword is null
            ? builder.Build()
            : builder
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
                $"Postgres with the sourced init scripts did not accept a TCP connection within {StartTimeout} " +
                $"(container state: {container.State}). An init script ending the entrypoint shell looks like this.");
        }
    }

    private static async Task<long> WaitForExitAsync(
        IContainer container, CancellationTokenSource timeout, CancellationToken cancellationToken)
    {
        try
        {
            // IContainer.State is a cached snapshot, so probe the container itself: an exec into a
            // stopped container is refused by Docker, a running one answers.
            while (await IsRunningAsync(container, cancellationToken))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"The container was still running after {StartTimeout}; the fail-closed guard did not stop it.");
        }

        return await container.GetExitCodeAsync(cancellationToken);
    }

    private static async Task<bool> IsRunningAsync(IContainer container, CancellationToken cancellationToken)
    {
        try
        {
            var probe = await container.ExecAsync(["true"], cancellationToken);
            return probe.ExitCode == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<string> Query(IContainer container, string sql, CancellationToken cancellationToken)
    {
        var result = await container.ExecAsync(
            ["psql", "-h", "127.0.0.1", "-U", "postgres", "-tAc", sql], cancellationToken);
        result.ExitCode.Should().Be(0, "the query must run");
        return result.Stdout.Trim();
    }
}
