// Issue #21 (isolation-test skill): one lazy, shared Postgres container for every test in this
// assembly that needs one, the same shape tests/Decisya.Infrastructure.Persistence.Tests uses
// (#22). The container starts on the first CreateDatabaseAsync/CreateEmptyDatabaseAsync call, so
// the unit lane (TenantMembershipGateConstraintMatchingTests, TenancyModuleBoundaryTests, the S-1
// source check) never touches Docker.
[assembly: Xunit.AssemblyFixture<Decisya.TestInfrastructure.PostgresFixture>]
