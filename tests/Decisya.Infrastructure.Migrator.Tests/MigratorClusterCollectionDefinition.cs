namespace Decisya.Infrastructure.Migrator.Tests;

/// <summary>
/// Postgres roles are cluster-wide, and every test database lives in the one PostgresFixture
/// cluster, so two parallel <c>MigrationRunner.RunAsync</c> calls race on CREATE ROLE / ALTER ROLE
/// (23505 on pg_authid_rolname_index, XX000 "tuple concurrently updated"). Production runs one
/// migrator; the test classes that run it against the shared cluster therefore run serially.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MigratorClusterCollectionDefinition
{
    public const string Name = "Migrator runs against the shared Postgres cluster";
}
