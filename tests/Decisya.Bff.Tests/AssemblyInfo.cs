[assembly: Xunit.AssemblyFixture<Decisya.Bff.Tests.KeycloakBffFixture>]
[assembly: Xunit.AssemblyFixture<Decisya.Bff.Tests.RedisFixture>]
// Several Integration classes drive a real login (dev-alice/dev-bob) against the one
// shared KeycloakBffFixture container. Concurrent logins for the same user against the
// same Keycloak instance can race on its own auth-session state (an SSO session from one
// test's login can turn a later test's /authorize GET into an "already logged in"
// interstitial instead of the login form). Serializing test collections avoids that,
// the same way a single Testcontainers Keycloak is meant to be used one flow at a time.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
