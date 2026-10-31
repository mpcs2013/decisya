---
name: testcontainers
description: "How to write Testcontainers integration tests with xUnit v3: traits and the CI project list, lazy assembly fixtures, canary secrets, pinned images, bounded waits and container hygiene. Use whenever a test touches a database, an identity provider, a cache or another container."
---
# testcontainers

Part of the dotnet pack. Integration tests run real containers; a mocked dependency is not an integration test. The project's `tests` addendum lists its own runnable examples and image pins.

## Traits and CI
- Mark every container test `[Trait("Category", "Integration")]`, and keep a guard test in each test project that fails when a container test lacks the trait. The unit step in CI and the pre-push check exclude that trait. CI's integration job runs only the projects it lists; a new project with Integration tests must be added there, or its tests never run in CI.
- Run locally per project: `dotnet test --project tests/<Project> --filter-trait "Category=Integration"`. Never use a solution-wide inclusion filter: every project without the trait exits 8.

## Fixtures (xUnit v3)
- xUnit v3 initialises assembly fixtures before it applies trait filters, so `InitializeAsync` of an assembly fixture runs even in the unit step. Keep it free of containers and secrets; start containers and resolve secrets lazily in the first Integration test that needs them.
- Secrets for test containers are generated per run (a command in CI, a fixture-generated value locally), validated, and never printed. In CI a missing value fails the test; it is never silently generated.
- Canary values that must never appear in output are built at run time, so no secret-shaped literal sits in source.
- A test that starts a host which must fail starts it directly, not through a shared test host: the host's entry-point thread can race the failing host's disposal.

## Containers
- Images come from one pinned list in the test code (tag plus a multi-architecture index digest, so every supported architecture runs).
- Every container is throwaway: random names and no named volumes. Never mount a host path, never mount the Docker socket, and do not disable the resource reaper.
- Bound every wait (start, readiness, restart) with a timeout and a message that says what did not become ready. A wait strategy that listens for a log line may never fire on a restart; poll readiness instead.
- On failure, assert on a bounded excerpt of container output, never the whole log.
