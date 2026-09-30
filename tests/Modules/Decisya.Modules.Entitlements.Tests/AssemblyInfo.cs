// Issue #23 (isolation-test skill): one lazy, shared Postgres container for every test in this
// assembly that needs one, the same shape Decisya.Modules.Tenancy.Tests uses (#21). The
// container starts on the first CreateEmptyDatabaseAsync call, so the unit and architecture
// lanes never touch Docker.
[assembly: Xunit.AssemblyFixture<Decisya.TestInfrastructure.PostgresFixture>]
