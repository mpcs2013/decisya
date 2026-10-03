# Architecture note – Dependabot PRs pass commitlint, unmergeable bumps stop (#113)

Requirements: `docs/requirements/phase-0/dependabot-ci.md`. No module, contract, cross-module
dependency or runtime data flow changes; no C4 or NetArchTest change. The decisions below are CI
and repository-configuration decisions that G4/G5 need fixed before they start.

## D1 – How the two commitlint steps are skipped

Both steps in `build-test` ("Commit messages (Conventional Commits)" and "Commit messages verdict
and mirror drift check") get the **same** step-level condition, replacing `github.event_name ==
'pull_request'`:

```yaml
if: >-
  github.event_name == 'pull_request' && !(
    github.event.pull_request.user.login == 'dependabot[bot]' &&
    github.actor == 'dependabot[bot]' &&
    startsWith(github.head_ref, 'dependabot/'))
```

- Same triple as the `claude-config` "Pipeline gates" exemption (G6-80-05): PR author, actor of
  the triggering event, Dependabot head branch. One rule for both exemptions in `ci.yml`.
- **Author** is the deciding fact (G1 Story 1, scenario 3). Branch name alone is never enough.
- **Actor** narrows it: a human push to a Dependabot branch triggers `synchronize` with that human
  as `github.actor`, so the whole range, the human commit included, is linted. That PR then fails on
  Dependabot's own commit, which is intended: the documented route for a hand-fixed bump is a new
  `issue/<n>-bump-…` branch (`docs/runbooks/main-ruleset.md`). A re-run keeps the original
  `github.actor` (only `github.triggering_actor` changes), so re-running a Dependabot run stays
  exempt. "Update branch" by Marco makes him the actor; the runbook already says
  `@dependabot rebase`.
- Evaluated in the `if:` expression, never interpolated into a shell, so no injection surface.
  No `pull_request_target`, no new permissions.
- The job is not skipped, only the two steps, so the required check `build-test` still reports.

**Mirror drift:** the two conditions must be byte-identical. If only the first step were skipped,
`OUTCOME` would be `skipped`, differ from the mirror's verdict and fail the job as "mirror drift".
G5 pins the equality.

**Rejected:**
- *Every commit in base..head authored by Dependabot* (shell check in the drift step): commit
  author/committer fields are client-set, so it adds a script without adding trust; the actor check
  already catches the human-push case, and the local commit-msg hook lints Marco's commits.
- *commitlint `ignores`*: matches on message text (anyone can write it), applies to every PR, and
  would have to be mirrored in `.claude/scripts/commitlint.py` and the goldens
  (`.claude/tests/fixtures/commitlint/`), widening the drift surface.

## D2 – `dependabot.yml` ignore entries

npm entry (`directory: /src/Decisya.Web`):

```yaml
    # TS 7 and ESLint 10 fail `npm ci` (ERESOLVE): typescript-eslint and eslint-plugin-jsx-a11y
    # peer ranges do not allow them yet (#106, #107). Lift per docs/runbooks/main-ruleset.md.
    ignore:
      - dependency-name: "typescript"
        update-types: ["version-update:semver-major"]
      - dependency-name: "eslint"
        update-types: ["version-update:semver-major"]
```

docker entry (`directory: /.devcontainer/engine`):

```yaml
    # Keycloak must change in images.Dockerfile and src/Decisya.AppHost/ContainerImages.cs in one
    # PR (ContainerImageParityTests); a Dependabot bump touches only the Dockerfile (#105).
    # Bump by hand per docs/runbooks/ci-security-gates.md ("Image CVE scan").
    ignore:
      - dependency-name: "keycloak/keycloak"
```

No `update-types` on the Keycloak rule: versions and digest-only bumps are both ignored. Dependabot
names the docker dependency without the registry (`keycloak/keycloak`, not
`quay.io/keycloak/keycloak`), as G3 G4-113-01 and #105's title "bump keycloak/keycloak" confirm. Postgres in the same file is unaffected. Compensating control
for the ignored image: the weekly image CVE scan (ADR-0015) still scans `ContainerImages.cs`.

## D3 – Tests (G5)

New `.claude/tests/test_dependabot.py`, stdlib only like the other files there (no PyYAML), run by
the `claude-config` job (`unittest discover -s .claude/tests -p "test_*.py"`) and by the pre-push
check:

1. `dependabot.yml`: splits the `updates:` list into `- package-ecosystem:` blocks; fails if the
   blocks cannot be found (the "no longer parses" case). The npm `/src/Decisya.Web` block has both
   major-only rules; the docker `/.devcontainer/engine` block has the Keycloak rule with no
   `update-types`; no other block ignores `typescript`, `eslint` or Keycloak.
2. `ci.yml`: both commitlint steps carry the identical `if:` from D1, containing all three
   `dependabot[bot]`/`dependabot/` terms; `build-test` itself has no job-level `if:`.

The "bad human commit still fails" Done-when is covered by the unchanged `test_commitlint.py`
goldens plus the verify step on the PR.

## D4 – Runbook

`docs/runbooks/main-ruleset.md`, section "Dependabot under a strict ruleset": replace the
commitlint/grouping advice (now obsolete for Dependabot) with the exemption rule from D1, the three
ignores with their reasons, how to lift each (TypeScript/ESLint: when typescript-eslint and
eslint-plugin-jsx-a11y peer ranges accept the new major; Keycloak: bump `ContainerImages.cs` and
`images.Dockerfile` in one PR), and "do not push to a Dependabot branch; use `@dependabot rebase`".
`docs/runbooks/ci-security-gates.md` line on the Keycloak digest: one sentence that Dependabot no
longer opens that PR.

## ADR

None. A scoped CI exemption and update-ignore rules; no ADR or invariant changes. ADR-0014
(pinning) and ADR-0015 (image scan) stay as they are.

<!-- gate: G2 | verdict: PASS | issue: #113 -->
