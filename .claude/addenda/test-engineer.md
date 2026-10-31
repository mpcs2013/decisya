# test-engineer addendum (Decisya)

Read with Norma's `test-engineer` agent and its `traceability` skill. This file adds Decisya's frameworks, lanes and commands (#246, ADR-0023).

## Frameworks and lanes
- The pyramid: xUnit v3 unit tests, NetArchTest architecture tests, Testcontainers integration tests, Playwright E2E with axe-core, k6 load baselines.
- Assertions use AwesomeAssertions (`using AwesomeAssertions;`); never FluentAssertions (v8+ needs a paid commercial license).
- Every test class carries `[Trait("Category", "Unit|Architecture|Integration|Contract|E2E|AppHost")]`.
- Integration tests use Testcontainers (Postgres, Redis, Keycloak) and a real BFF/API host; no mocked DbContext. See `.claude/addenda/testcontainers.md`.
- Every tenant-scoped feature has two-tenant isolation tests (`isolation-test` skill).
- Every auth change has negative tests: expired token, wrong audience, `alg=none`, missing antiforgery header.
- E2E runs in Firefox and Chromium; axe reports zero violations. You have no `npm run`: frontend-dev or the main session runs the E2E lane.
- Report flaky tests as issues; never retry-loop them silently.

## Commands (Microsoft.Testing.Platform, `global.json`)
- Filter with `--filter-trait` / `--filter-not-trait "Category=…"` and scope with `--project <test project>`: MTP fails every assembly in which zero tests run (exit code 8), so a solution-wide inclusion filter fails as soon as one project has no test in that category.
- Add `--minimum-expected-tests` when a lane must not be empty.
- Never `--logger` (MTP rejects it), and never a report or output option such as `--report-xunit-trx` or `--results-directory`: the hook denies `dotnet` options that write files, and CI writes the TRX.
- Unit lane, as CI and the pre-push hook: `dotnet test --filter-not-trait "Category=Integration" --filter-not-trait "Category=E2E" --filter-not-trait "Category=AppHost"`.

## Traceability
List the tests with *Test Explorer* grouped by class (VS 2026) | `dotnet test --no-build --list-tests` (CLI).
