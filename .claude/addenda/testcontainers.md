# testcontainers addendum (Decisya)

Read with the dotnet pack's `testcontainers` skill (the "tests" addendum it names): Decisya's CI list, restore, fixtures, pins and runnable examples (#246, ADR-0023). Lessons from #17, #59 and #72.

## Traits and CI
- Every container test carries `[Trait("Category", "Integration")]`. CI's unit step and the pre-push hook exclude it. CI's Integration step runs only the projects listed in `.github/workflows/ci.yml`; `.claude/tests/test_ci_integration.py` fails until a new project with Integration tests is added there.
- Run per project: `dotnet test --project tests/<Project> --filter-trait "Category=Integration"`. Never a solution-wide inclusion filter: every project without the trait exits 8.

## Restore
- Restores need the private Ostium feed (#164): a workstation `read:packages` credential at user level; in CI the job token on restore steps only.
- The solution restores in locked mode (`DECISYA_LOCKED_RESTORE=true`), so a container test that builds a project as a subprocess needs that project's `packages.lock.json` (`BannedApiFixture` has one).
- `CaddyRootTrustTests` has an Api.Tests copy against `Ostium.Backchannel.BackchannelRoot`; the BFF copy stays until the BFF switch.

## Fixtures and secrets
- Start containers and resolve secrets lazily in the first Integration test that needs them (`KeycloakRealmFixture.EnsureStartedAsync`), never in an assembly fixture's `InitializeAsync`.
- Secrets for test containers: generated per run (`openssl rand -hex` in CI, a fixture-generated value on the host), validated, never printed; in CI a missing value fails the test.
- Canary values that must never appear in output are built at run time (`Canaries`).

## Images
Images come from `ContainerImages` (tag plus sha256), the same pins as the AppHost.

## Runnable examples
| Rule | Example | Run |
| --- | --- | --- |
| Lazy assembly fixture, secret resolution per branch | `tests/Decisya.Identity.Tests/KeycloakRealmFixture.cs`, `KeycloakRealmFixtureSecretResolutionTests.cs` | `dotnet test --project tests/Decisya.Identity.Tests --filter-not-trait "Category=Integration"` |
| Bounded restart wait, bounded log excerpt | `tests/Decisya.Identity.Tests/RealmReimportTests.cs` | `dotnet test --project tests/Decisya.Identity.Tests --filter-trait "Category=Integration"` (Docker) |
| Every container test carries the trait; CI project list | `tests/Decisya.Identity.Tests/IntegrationCategoryTraitGuardTests.cs`, `.claude/tests/test_ci_integration.py` | `python -m unittest discover -s .claude/tests -p "test_ci_integration.py"` |
| Run-time canaries | `tests/Decisya.Identity.Tests/Canaries.cs` | any of the above |
