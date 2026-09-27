---
name: testcontainers
description: "How to write Decisya's Testcontainers integration tests with xUnit v3: traits and the CI project list, lazy assembly fixtures, canary secrets, pinned images, bounded waits and container hygiene. Use whenever a test touches Postgres, Redis, Keycloak or another container."
---
# testcontainers

Integration tests run real containers (CLAUDE.md: Testcontainers for anything touching Postgres, Redis or Keycloak). Lessons from #17, #59 and #72.

## Traits and CI
- Mark every container test `[Trait("Category", "Integration")]`. CI's unit step and the pre-push hook exclude that trait. CI's Integration step runs only the projects listed in `.github/workflows/ci.yml`, and `test_ci_integration.py` fails until a new project with Integration tests is added there.
- Run locally per project: `dotnet test --project tests/<Project> --filter-trait "Category=Integration"` (GETTING-STARTED §4). Never use a solution-wide inclusion filter: every project without the trait exits 8.

## Fixtures (xUnit v3)
- xUnit v3 initialises assembly fixtures before it applies trait filters, so `InitializeAsync` of an assembly fixture runs even in the unit step. Keep it free of containers and secrets; start containers and resolve secrets lazily in the first Integration test that needs them (`KeycloakRealmFixture.EnsureStartedAsync`).
- Secrets for test containers: generated per run (`openssl rand -hex` in CI, a fixture-generated value on the host), validated, never printed. In CI a missing value fails the test; it is never silently generated.
- Canary values that must never appear in output are built at run time (`Canaries`), so no secret-shaped literal sits in source.

## Containers
- Images come from `ContainerImages` (tag plus sha256), the same pins as the AppHost.
- Every container is throwaway: random names and no named dev volumes. Never mount a host path, never mount the Docker socket, and do not disable Ryuk.
- Bound every wait (start, readiness, restart) with a timeout and a message that says what did not become ready. A library wait strategy that listens for a log line may never fire on a restart; poll readiness instead.
- On failure, assert on a bounded excerpt of container output, never the whole log.

## Runnable examples
These tests are the canonical examples; CI runs them, so they cannot drift from the rules above.

| Rule | Example | Run |
| --- | --- | --- |
| Lazy assembly fixture, secret resolution per branch | `tests/Decisya.Identity.Tests/KeycloakRealmFixture.cs`, `KeycloakRealmFixtureSecretResolutionTests.cs` | `dotnet test --project tests/Decisya.Identity.Tests --filter-not-trait "Category=Integration"` |
| Bounded restart wait, bounded log excerpt | `tests/Decisya.Identity.Tests/RealmReimportTests.cs` | `dotnet test --project tests/Decisya.Identity.Tests --filter-trait "Category=Integration"` (Docker) |
| Every container test carries the trait; CI project list | `tests/Decisya.Identity.Tests/IntegrationCategoryTraitGuardTests.cs`, `.claude/tests/test_ci_integration.py` | `python -m unittest discover -s .claude/tests -p "test_ci_integration.py"` |
| Run-time canaries | `tests/Decisya.Identity.Tests/Canaries.cs` | any of the above |
