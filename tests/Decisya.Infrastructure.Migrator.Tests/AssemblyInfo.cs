// Issue #21 (G4-21-05, testcontainers skill): one lazy, shared Postgres container for every
// test in this assembly that needs one. The container starts on the first
// CreateEmptyDatabaseAsync call, so the Unit lane (password validation, ScramSha256Verifier,
// the structural/log-template checks) never touches Docker.
[assembly: Xunit.AssemblyFixture<Decisya.TestInfrastructure.PostgresFixture>]
