// Issue #341 (isolation-test and testcontainers skills): one lazy, shared Postgres container for every
// test in this assembly that needs one, the same shape Decisya.Modules.Entitlements.Tests uses (#23).
// The container starts on the first CreateEmptyDatabaseAsync call, so the unit and architecture lanes
// never touch Docker.
[assembly: Xunit.AssemblyFixture<Decisya.TestInfrastructure.PostgresFixture>]
