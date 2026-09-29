using Decisya.TestInfrastructure;
using Microsoft.EntityFrameworkCore;

namespace Decisya.AppHost.Tests;

/// <summary>
/// Issue #21 (0.09), G2 "Database, roles, migrator and tests": a smoke test for
/// <see cref="PostgresFixture.CreateEmptyDatabaseAsync"/>. It needs only a real Postgres
/// container (Testcontainers.PostgreSql), not DCP or the Aspire CLI bundle, but stays in
/// this project — under its existing "Docker running" contract — rather than a new
/// Integration-lane test project outside platform-dev's lane for this issue.
/// </summary>
[Trait("Category", "AppHost")]
public class PostgresFixtureEmptyDatabaseTests
{
    [Fact]
    public async Task CreateEmptyDatabaseAsync_returns_a_connection_string_to_a_genuinely_empty_database()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var fixture = new PostgresFixture();

        var connectionString = await fixture.CreateEmptyDatabaseAsync(cancellationToken);

        var options = new DbContextOptionsBuilder().UseNpgsql(connectionString).Options;
        await using var context = new DbContext(options);

        await context.Database.OpenConnectionAsync(cancellationToken);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM information_schema.tables " +
            "WHERE table_schema NOT IN ('pg_catalog', 'information_schema')";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        var tableCount = Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);

        tableCount.Should().Be(
            0, "CreateEmptyDatabaseAsync should hand back a fresh database with no schema and no tables");
    }

    [Fact]
    public async Task CreateEmptyDatabaseAsync_creates_a_distinct_database_on_each_call()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var fixture = new PostgresFixture();

        var first = await fixture.CreateEmptyDatabaseAsync(cancellationToken);
        var second = await fixture.CreateEmptyDatabaseAsync(cancellationToken);

        // Never compares or prints the full connection strings — they carry the container's
        // superuser password (G3 T-15) — only the trailing "Database=..." segment.
        var firstDatabase = first[(first.LastIndexOf("Database=", StringComparison.Ordinal) + "Database=".Length)..];
        var secondDatabase = second[(second.LastIndexOf("Database=", StringComparison.Ordinal) + "Database=".Length)..];

        firstDatabase.Should().NotBe(secondDatabase);
    }
}
