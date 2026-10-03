# Phase 0 – Dependabot PRs pass commitlint, and unmergeable bumps stop

## Issue #113 — ci: let Dependabot PRs pass commitlint and stop unmergeable Dependabot bumps

### Scope note (role framing)

No tenant-facing capability. The only actor is **Marco as maintainer**, who merges or closes
Dependabot PRs. **Entitlement plan: N/A** for every story (CI and repository configuration, no
`module.feature` key applies), as in `docs/requirements/phase-0/roster-docs.md`.

Scope: skip the commit-message step of the `build-test` job for PRs opened by `dependabot[bot]`
only; `.github/dependabot.yml` ignores the TypeScript and ESLint majors and the devcontainer
Keycloak image; a pinning test; a runbook note. The other `build-test` steps stay required for
Dependabot PRs. Handling of the 7 open Dependabot PRs is manual, by Marco after merge, and is
outside this scope.

Facts (data): #104 and #112 fail `body-max-line-length` (100); #109 fails `subject-case`
("chore: Bump NodaTime"); #105 changes Keycloak only in `.devcontainer/engine/images.Dockerfile`
and fails `ContainerImageParityTests` because `src/Decisya.AppHost/ContainerImages.cs` must change
with it; #106 (TypeScript 7) and #107 (ESLint 10) fail `npm ci` with ERESOLVE.

---

### Story 1 — A Dependabot PR is not failed by commitlint

As Marco (maintainer), I want `build-test` to skip the Conventional Commits check for PRs opened by
`dependabot[bot]`, so that bot-written subjects and long URL body lines no longer block an
otherwise green dependency bump. (Done when 1)

```gherkin
Feature: Commitlint is skipped for Dependabot PRs only

  Scenario: Dependabot PR with a capitalised subject and a long URL line passes build-test
    Given a pull request opened by "dependabot[bot]"
    And its commit subject is "chore: Bump NodaTime from 3.2.0 to 3.3.0"
    And its commit body has a 140-character URL line
    When the build-test job runs
    Then the steps "Commit messages (Conventional Commits)" and
      "Commit messages verdict and mirror drift check" do not fail the job
    And the job's result depends on its remaining steps only

  Scenario: Every other build-test step still runs for a Dependabot PR
    Given a pull request opened by "dependabot[bot]"
    When the build-test job runs
    Then build, tests, ContainerImageParityTests and every non-commit-message step run
      as for any other PR

  Scenario: The skip is decided by the PR author, not by spoofable values
    Given a pull request opened by a human account
    And its head branch is named "dependabot/npm_and_yarn/foo" or its commits carry a
      Dependabot author name
    When the build-test job runs
    Then commitlint is not skipped

  Scenario: The check stays required
    Given the branch protection on main
    When this change is merged
    Then the required check is still named "build-test" and is still reported for
      Dependabot PRs (skipped steps, not a skipped job)
```

---

### Story 2 — A human PR with a bad commit message still fails

As Marco, I want commitlint to keep failing non-Dependabot PRs, so that the skip does not weaken
the Conventional Commits rule for my own and agent commits. (Done when 2)

```gherkin
Feature: Commitlint stays enforced for non-Dependabot PRs

  Scenario: Bad subject on a human PR fails build-test
    Given a pull request opened by a non-Dependabot account
    And a commit subject "Fix: Stuff" (uppercase start)
    When the build-test job runs
    Then "Commit messages (Conventional Commits)" fails and so does build-test

  Scenario: Over-long header on a human PR fails build-test
    Given a pull request opened by a non-Dependabot account
    And a commit header of 101 characters
    When the build-test job runs
    Then build-test fails

  Scenario: The local mirror is unchanged
    Given .claude/scripts/commitlint.py
    When it checks the same bad messages locally
    Then it still rejects them, and the mirror drift check still compares it with the CI rules
```

---

### Story 3 — Dependabot stops proposing bumps that cannot merge

As Marco, I want `.github/dependabot.yml` to ignore the TypeScript and ESLint major versions and
the devcontainer Keycloak image, so that no new PR like #105, #106 or #107 is opened until I
choose to migrate. (Done when 3)

```gherkin
Feature: dependabot.yml suppresses known-unmergeable bumps

  Scenario: TypeScript and ESLint majors are ignored
    Given .github/dependabot.yml
    When its npm ecosystem entry is parsed
    Then "typescript" and "eslint" each have an ignore rule for
      version-update:semver-major
    And minor and patch updates of both are not ignored

  Scenario: The devcontainer Keycloak image is ignored
    Given .github/dependabot.yml
    When the entry covering .devcontainer/engine is parsed
    Then the Keycloak image is ignored for all update types
    And the reason (ContainerImages.cs must change together with the Dockerfile) is in a
      YAML comment next to the rule

  Scenario: A test pins the configuration
    Given the .claude test suite
    When the dependabot.yml pinning test runs against the file
    Then it fails if any of the three ignore rules above is removed
    And it fails if the file no longer parses

  Scenario: The runbook explains how to lift an ignore
    Given the runbook note added by this issue
    When Marco reads it
    Then it states why each ignore exists, that the AppHost image constant and the
      devcontainer Dockerfile must be bumped in one PR, and when the TypeScript and
      ESLint ignores can be removed (typescript-eslint and eslint-plugin-jsx-a11y
      peer ranges allow the new major)
```

---

## Non-functional requirements

No new measurable target; no row added to `docs/requirements/nfr.md`.

## Open questions

None.

## Notes for G2/G3 (non-blocking)

- The author test must use the PR author (for example `github.event.pull_request.user.login`),
  not the branch name, commit author, or `github.actor` (a re-run by Marco changes the actor).
  Story 1's third scenario fixes this behaviour; the mechanism is G2's choice.
- Do not use `pull_request_target` or add permissions to implement the skip (G3 to confirm).
- "Verified by `@dependabot recreate` on #109" is a manual post-merge check by Marco.

<!-- gate: G1 | verdict: PASS | issue: #113 -->

## Traceability

Tests: `.claude/tests/test_dependabot.py` (new, 8 tests), `.claude/tests/test_commitlint.py` (unchanged goldens in `.claude/tests/fixtures/commitlint/`), `ContainerImageParityTests` (`tests/**/ContainerImageParityTests.cs`). Run 2026-10-03: `python -m unittest discover -s .claude/tests -p "test_*.py"` gives 252 OK (2 skipped). G4 evidence: build 0 errors, unit 1372 passed. No test is missing. Scenarios the repository cannot prove are marked manual and are listed in `docs/runbooks/main-ruleset.md` ("Commitlint and Dependabot PRs" and "Ignored Dependabot updates").

Test names below are Python `unittest` names in `test_dependabot.py` unless stated.

### Story 1 (Done when 1)

| Acceptance criterion | Test(s) |
| --- | --- |
| Dependabot PR with capitalised subject and 140-char URL line passes build-test (both commitlint steps do not fail the job) | `CommitlintExemptionTests.test_both_steps_carry_exactly_the_exemption`; `DetectorTests.test_weakened_conditions_do_not_match` (red case). End to end: manual, `@dependabot recreate` on #109 after merge, because only a real Dependabot PR on GitHub exercises the runner expression |
| Job result depends on remaining steps only | `CommitlintExemptionTests.test_only_commitlint_continues_on_error` (verdict step has no `continue-on-error`) |
| Every other build-test step still runs for a Dependabot PR | `CommitlintExemptionTests.test_both_steps_carry_exactly_the_exemption` (only the two named steps carry the `if:`); `ContainerImageParityTests` runs in the unit lane of build-test. Whole-job run: manual (step 4 of the post-merge checks) |
| Skip decided by PR author, not by branch name or commit author | `CommitlintExemptionTests.test_both_steps_carry_exactly_the_exemption` (requires `user.login`, `github.actor` and `head_ref` together as one whole expression); `DetectorTests.test_weakened_conditions_do_not_match` (a `||` or a lone `actor` check does not match) |
| Check stays required and reported (skipped steps, not a skipped job) | `CommitlintExemptionTests.test_build_test_has_no_job_level_if`. Ruleset still names `build-test`: unchanged by this issue, covered by `.claude/tests/test_ruleset.py` |

### Story 2 (Done when 2)

| Acceptance criterion | Test(s) |
| --- | --- |
| Bad subject ("Fix: Stuff") on a human PR fails | `test_commitlint.py` `test_every_fixture_matches_golden_output_and_exit_code` (goldens `first-word-capital`, `first-word-scope-capital` and others reject capitalised subjects); `CommitlintExemptionTests.test_both_steps_carry_exactly_the_exemption` (human PRs are not exempt) |
| 101-char header on a human PR fails | `test_commitlint.py` `test_every_fixture_matches_golden_output_and_exit_code` (goldens `header-101` (rejected) and `header-100` (accepted)); exemption pinned as above. Live failure on a real human PR: manual, observed on any PR with a bad message |
| Local mirror unchanged, still rejects, drift check still compares to CI rules | `test_commitlint.py` `test_every_fixture_matches_golden_output_and_exit_code`, `test_ci_pins_the_version_the_goldens_came_from`; goldens untouched by this issue (`git status` shows no change under `.claude/tests/fixtures`) |

### Story 3 (Done when 3)

| Acceptance criterion | Test(s) |
| --- | --- |
| `typescript` and `eslint` ignored for `version-update:semver-major`; minor and patch not ignored | `DependabotIgnoreTests.test_ignore_rules_are_exact_and_scoped` (exact rule list per entry, so any `update-types` widening fails) |
| Keycloak image ignored for all update types, reason in a YAML comment | `DependabotIgnoreTests.test_ignore_rules_are_exact_and_scoped` (rule `keycloak/keycloak` with no `update-types`; Postgres not ignored); `DetectorTests.test_rules_parse_and_registry_name_differs`. YAML comment next to the rule: manual (review of `git diff`; a test cannot judge a comment's wording). Dependabot actually honours the rule: manual (post-merge steps 1 to 3: close #105, confirm no new PR) |
| A test pins the config; fails if a rule is removed or the file no longer parses | `DependabotIgnoreTests.test_ignore_rules_are_exact_and_scoped`; `DetectorTests.test_unreadable_dependabot_layout_fails`; red check recorded in G4 (`origin/main` files give no ignore rules) |
| Runbook explains each ignore, the one-PR Keycloak bump, and when the TypeScript/ESLint ignores can go | manual: documentation, reviewed in `docs/runbooks/main-ruleset.md` ("Ignored Dependabot updates" table) |

### Manual post-merge steps for Marco

1. `@dependabot recreate` on #109, #104, #112: `build-test` passes with the commitlint steps skipped (Done when 1).
2. Close #105, #106, #107 and confirm Dependabot opens no new PR for them (Done when 3).
3. Confirm a non-Dependabot PR with a bad message still fails `build-test` (Done when 2), for example the next human PR or a throwaway draft.
4. Record the result in `docs/ai/pipeline/113.md` (the runbook asks for it; the manifest is not edited by G5).

### Notes

- The workflow expression is compared as text. No test evaluates it with a GitHub runner, and `actionlint` is not installed on the host; CI's `claude-config` job covers syntax.

<!-- gate: G5 | verdict: PASS | issue: #113 -->
