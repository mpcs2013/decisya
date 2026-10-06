# Branch ruleset (main)

- Owner: devops · Last verified: 2026-09-27 (updated 2026-10-03 for #28; the commands added for #28 are marked **(unverified)**, because agents cannot run `gh api` or `gh run`)
- When to use: applying, changing or verifying the GitHub ruleset that protects `main` (id `23835975`), after `.github/rulesets/main.json` changes, at each phase exit, or when a required check is renamed, added or removed.
- Threat model: `docs/security/threat-models/main-ruleset.md`.

**Only Marco applies or changes the live ruleset, through the GitHub web UI or `gh api` under his own login. Agents never call the rulesets or branch-protection API** (`agent.gh-destructive` in `.claude/hooks/agent_boundaries.py` denies `gh api` to every agent session; this is a guardrail, not a boundary — see "What the hooks do" in `docs/runbooks/issue-pipeline.md`). Agents may edit `.github/rulesets/main.json` (the desired state, reviewed like any other file) and the drift test in `.claude/tests/`; they never PUT, PATCH or DELETE against `rulesets/*` or `rules/branches/*`, and never ask you to run such a command without showing you the diff first.

`main.json` mirrors the live ruleset except for one intentional addition: `changes` is a required context that is not yet required live (issue #80, G4-80-03 option (a)). Marco applies the file; from then on the two match exactly, less GitHub's read-only fields (`id`, `source`, `node_id`, `_links`, `created_at`, `updated_at`, `current_user_can_bypass`), which the file never carries.

## Apply

Always take the file from `origin/main` after it has merged, or from a reviewed PR head only after reading its `.github/rulesets` diff — never from an agent's working copy or a suggestion that skips that read. This closes the gap where an injected agent edits `main.json` on a branch and hands Marco a PUT command for content he never reviewed (G6-80-10). Because `changes` already reports on `main` today, applying from this issue's own PR head — after reading its diff — is fine even before the PR merges (G6-80-09).

| Visual Studio 2026 / GitHub web UI | CLI |
| --- | --- |
| VS 2026 has no UI for rulesets; use the browser. On `github.com/mpcs2013/decisya`: *Settings → Rules → Rulesets → main*. Use *Import a ruleset* (or edit the existing one) and paste the contents of `.github/rulesets/main.json` from `origin/main` (or the reviewed PR head), then *Save changes* | `git show origin/main:.github/rulesets/main.json > <scratch file outside the repo>` (or the PR head's copy, after `git diff`), then `gh api -X PUT repos/mpcs2013/decisya/rulesets/23835975 --input <that scratch file>` |

Read the file before applying it; the PUT is destructive to whatever the UI shows as current. Marco runs this step himself; an agent may hand him the exact command and the file diff, never run it.

## Verify (after every apply, and at each phase exit)

Four read-only checks (G4-80-11); none of them may use an Administration-scoped PAT, and none runs in CI.

| # | Check | Visual Studio 2026 / GitHub web UI | CLI | Expected |
| --- | --- | --- | --- | --- |
| 1 | Required checks | *Settings → Rules → Rulesets → main*, read the "Require status checks to pass" rule | `gh api repos/mpcs2013/decisya/rules/branches/main` | Lists `deletion`, `non_fast_forward`, `pull_request` and `required_status_checks`, and `required_status_checks` lists at least the floor set `build-test`, `claude-config`, `codeql`, `image-scan`, `realm-guard` (plus `changes`: six contexts total once #80 and #28 are applied) |
| 2 | Enforcement and bypass | Same page, header shows "Active" and the "Bypass" list | `gh api repos/mpcs2013/decisya/rulesets/23835975` | `enforcement: "active"`, `bypass_actors: []` |
| 3 | A failing check blocks merge | Open a throwaway PR from a scratch branch that makes one required check fail (for example a syntax error caught by `build-test`), look at the merge box | `gh pr create` from the same scratch branch, then `gh pr view --json mergeStateStatus,statusCheckRollup` | The PR page (and `mergeStateStatus`) shows "Merging is blocked"; close the PR without merging and delete the branch afterwards |
| 4 | Default workflow token permissions | *Settings → Actions → General → Workflow permissions*, read the selected option | `gh api repos/mpcs2013/decisya/actions/permissions/workflow` | "Read repository contents and packages permissions" is selected; `default_workflow_permissions: "read"`. This is what a workflow file with no top-level `permissions:` key would inherit (G6-80-03); `ci.yml` declares its own narrower `permissions:` today regardless |

Record the four outputs in the issue's G4 evidence (or, outside an issue, in a dated note in `docs/ai/` or the PR that changed the ruleset).

The file cannot see live state by itself (T80-07): these four checks are the manual control until a scheduled read-only comparison lands (`F-80-2`, tracked separately from #80).

## Renaming or removing a required check

A required context that stops reporting (renamed job, deleted job) is "Expected" forever and blocks every merge, including the fix — this fails closed, but do it in this order to avoid a self-inflicted lockout:

1. Add the new job/context to `ci.yml` and merge it — at this point both the old and the new context exist, and only the old one is required.
2. Once the new context has reported success on `main` at least once, update `.github/rulesets/main.json` to require the new context, and apply it (Apply, above).
3. Update `.github/rulesets/main.json` again to drop the old context, and apply that file too — from `origin/main` or a reviewed PR head (Apply, above) — **before** merging the PR below. Removing a floor-set context (`build-test`, `claude-config`, `codeql`, `image-scan`, `realm-guard`) also needs a change to the drift test's floor set, which makes the PR review-required (G4-80-02, G4-80-09).
4. Only once the live ruleset no longer requires the old context, merge the PR that removes the old job from `ci.yml`.

Never merge the PR that removes the old job while the live ruleset still requires its context: the context stops reporting and shows "Expected" forever, which blocks every merge, including the fix (T80-01) — it does not skip any check, it locks the repository out of merging.

## Adding `deploy-guards` as a required check (issue #120, one time, after the #120 PR merges)

`deploy-guards` (G3 G4-120-03) must always run, so a skipped run can never count as passed: its job has no `paths:` filter, no `needs` and no `if:` (`deploy/tests/test_ci_workflow.py` asserts it). Marco adds it **after** the #120 PR has merged and the job has reported success on `main` once, in the order of the "Adding a required check" steps above. The #120 PR does not change `.github/rulesets/main.json`, because requiring a context that has never reported on `main` blocks every merge.

| # | Step | VS Code / GitHub web UI (Firefox) | CLI (`gh`, Marco's own login) |
| --- | --- | --- | --- |
| 1 | Confirm `deploy-guards` reported on `main` | *Actions → CI →* the run for the merge commit *→ deploy-guards* is green and its "Home-network address scan" and "Deploy guards" steps ran (none is skipped) | `gh run list --workflow CI --branch main --limit 1`, then `gh run view <run-id> --json jobs --jq '.jobs[] \| select(.name=="deploy-guards") \| .conclusion'` → `success` **(unverified)** |
| 2 | Add the context to `.github/rulesets/main.json` in a reviewed PR: one more object in `required_status_checks` | edit the file in VS Code | add `{ "context": "deploy-guards", "integration_id": 15368 }` after the `image-scan` entry **(unverified)** |
| 3 | Apply and verify as in "Adding `image-scan`" steps 3 to 5 (Marco only) | same page: the list now has seven contexts | `gh api repos/mpcs2013/decisya/rules/branches/main` → `required_status_checks` has `deploy-guards` **(unverified)** |

## Adding `image-scan` as a required check (issue #28, one time)

`image-scan` (ADR-0015) is a new required context. `.github/rulesets/main.json` lists it from the #28 PR on, but the live ruleset requires it only after Marco applies the file. Follow the order of the section above: the job must report on `main` before it is required, or every merge waits for a context that never reported. Agents never run steps 4 and 5.

| # | Step | VS Code / GitHub web UI (Firefox) | CLI (`gh`, Marco's own login) |
| --- | --- | --- | --- |
| 1 | Confirm `image-scan` reported on `main` | *Actions → CI →* the run for the merge commit *→ image-scan* shows a green check | `gh run list --workflow CI --branch main --limit 1`, then `gh run view <run-id> --json jobs --jq '.jobs[] \| select(.name=="image-scan") \| .conclusion'` → `success` **(unverified)** |
| 2 | Run the scan once for real, not skipped | *Actions → CI → Run workflow* (branch `main`), then open *image-scan* and check that the step "Image CVE scan (High/Critical fail)" ran and passed (a skipped step shows a grey dash) | `gh workflow run CI --ref main`, then `gh run watch` **(unverified)** |
| 3 | Read the ruleset diff | VS Code: *Source Control → … → View History*, or the merged PR's *Files changed* → `.github/rulesets/main.json` | `git fetch origin`, then `git diff <pre-merge-sha> origin/main -- .github/rulesets/main.json` |
| 4 | Apply | *Settings → Rules → Rulesets → main*, edit or import with the contents of `origin/main`'s `main.json`, *Save changes* | `git show origin/main:.github/rulesets/main.json > <scratch file outside the repo>`, then `gh api -X PUT repos/mpcs2013/decisya/rulesets/23835975 --input <that file>` (the same command as Apply, above) |
| 5 | Verify (Verify checks 1 to 4, above) and record the output in #28's G4 evidence or a dated PR note | Same page: "Require status checks to pass" lists `changes`, `build-test`, `codeql`, `claude-config`, `realm-guard` and `image-scan` | `gh api repos/mpcs2013/decisya/rules/branches/main` → `required_status_checks` has those six contexts |

Step 2 matters because on a PR that does not touch the scan inputs the step is skipped by design and the job still reports success. Only a `workflow_dispatch`, a `schedule` run or a PR that changes a scan input (`ContainerImages.cs`, `images.Dockerfile`, `.github/image-scan/**`, `image_scan.py`, `ci.yml`) shows that the scan itself works.

After step 5, also check Dependabot (S-07): *Insights → Dependency graph → Dependabot*. Every entry must show a recent "last checked" time and no configuration error. The `cooldown` keys arrived with #28, and a key Dependabot rejects stops all updates for that ecosystem.

## `codeql` is skip-safe, and green does not mean "no alerts"

`codeql` is required but skips (reports success) on a private repository, on any PR that touches neither .NET nor SPA code (`needs.changes` outputs both `false`), and on `schedule` runs (the schedule payload carries no `repository.visibility`). This is intentional and documented in the drift test's skip-safe map, not a bug.

The repository is public, so on code PRs `codeql` really runs: it builds, analyses `csharp` and `javascript-typescript` with the `security-extended` suite, and uploads the SARIF. **A green `codeql` means the analysis ran and uploaded. It does not mean there are no alerts.** Alerts appear in *Security → Code scanning* and in GitHub's own code-scanning check on the PR, and they do not block merge, because the ruleset has no `code_scanning` rule. Adding one is tracked in #83: how that rule behaves when `codeql` is skipped is unverified, and it could lock every docs-only PR out of merging (T80-01 class). Read the Security tab after each merge until it exists.

| VS Code / GitHub web UI (Firefox) | CLI |
| --- | --- |
| *Security → Code scanning*, filter `is:open branch:main` | `gh api repos/mpcs2013/decisya/code-scanning/alerts?state=open` **(unverified)** |

If GitHub Advanced Security is turned off or the job is removed, do it in one PR:

- update `.github/rulesets/main.json` if the context's behavior or name changes;
- update the skip-safe map and floor set in the drift test;
- update this runbook's note.

This closes `F-80-1`.

## Break-glass (enforcement blocks a merge that must go through now)

Use only when a required check is wrong in a way that cannot be fixed forward in time (for example the checks API itself is degraded, or a required job is broken by a GitHub-side change). This is an exception, not a routine unblock — routine failures get fixed, not bypassed.

| Visual Studio 2026 / GitHub web UI | CLI |
| --- | --- |
| *Settings → Rules → Rulesets → main*, change "Enforcement status" to *Disabled*, save | `gh api -X PUT repos/mpcs2013/decisya/rulesets/23835975 --input <a scratch copy of origin/main's main.json with "enforcement": "disabled">` |

1. Start from `main.json` on `origin/main` (`git show origin/main:.github/rulesets/main.json > <scratch file outside the repo>`) and change only `enforcement` to `disabled` in that scratch copy. Keep the "disabled" copy outside the repository — a scratch directory, never a tracked path — so it can never be committed by accident. Record the reason and the time in the GitHub issue you are unblocking.
2. Merge the fix (or the PR that needed to go through).
3. Set `enforcement` back to `active` the same way, using `main.json` from `origin/main` (which always has `enforcement: "active"`) so the live state matches the file again.
4. Re-run Verify (above) and record the four outputs.
5. Record the whole break-glass episode — start time, reason, who, end time, verify output — in the GitHub issue.

**Preferred route: a temporary bypass for pull requests only.** It is narrower than disabling, because deletion and force-push protection stay enforced. It is live-only: `main.json` always keeps `bypass_actors: []`, and the drift test enforces that.
- A personal-account repository cannot name a single user as a bypass actor. Add the **Repository admin** role, which only Marco holds.
- Set its mode to **For pull requests only** (`bypass_mode: pull_request`). Otherwise the bypass also allows direct pushes to `main`.
- An agent using Marco's host credentials would also act as that role. It must never set this (see below).
- Afterwards, re-apply `main.json` from `origin/main`, whose explicit `[]` removes the bypass. Then re-run Verify check 2 to confirm `bypass_actors: []` (G6-80-14).

Only Marco performs a break-glass change; an agent never sets `enforcement` or `bypass_actors` to anything, in a live call or in a suggestion meant to be run without review.

## Read every workflow hunk before merging

The ruleset proves a check named `build-test`, `realm-guard`, `claude-config`, `codeql` or `image-scan` passed — it cannot tell "the check ran `main`'s definition of that job" apart from "the PR edited the job and its own edited version passed" (T80-05: every Actions workflow reports under the same GitHub App, so `integration_id` pinning does not separate a same-repo edit from the real job). Before merging any PR that touches `.github/workflows/**` or `.github/rulesets/**`:

| Visual Studio 2026 | CLI |
| --- | --- |
| *Git Changes* or the PR's *Files changed* tab in the browser: open every hunk under `.github/workflows/` and `.github/rulesets/`, read what each step now does | `git diff origin/main...HEAD -- .github/workflows .github/rulesets` |

Look specifically for: a required job's steps turned into a no-op or `exit 0`; a new job or workflow using a required job's name; `checks: write`, `statuses: write`, `permissions: write-all` added anywhere; a `pull_request_target` trigger; `paths`/`paths-ignore`/`branches`/`branches-ignore` added to `ci.yml`'s `pull_request` trigger. These paths are `REVIEW_REQUIRED_PATHS` in `gates.py`, so G3 and G6 already ran on them — this is Marco's own read before clicking merge, not a substitute for the gates.

## Dependabot under a strict ruleset

`strict_required_status_checks_policy: true` means every required check must have run against the PR's merge with the current tip of `main`, so an open Dependabot PR goes stale every time something else merges to `main`.

| Visual Studio 2026 / GitHub web UI | CLI |
| --- | --- |
| Open the Dependabot PR, comment `@dependabot rebase` | `gh pr comment <number> --body "@dependabot rebase"` |

`dependabot.yml` sets `commit-message: { prefix: "chore", include: "scope" }` on every `updates` entry, so Dependabot's commit headers are Conventional Commits (`chore(deps): …`).

### Commitlint and Dependabot PRs (#113)

Dependabot's own commits can break commitlint's limits: a capitalised subject (`Bump …`), or a body line with a long compare URL over the 100-character `body-max-line-length`. Both commitlint steps in `build-test` (the commitlint step and the verdict and mirror drift step) are therefore skipped when all three hold: `github.event.pull_request.user.login` is `dependabot[bot]`, `github.actor` is `dependabot[bot]`, and `github.head_ref` starts with `dependabot/`. This is the same triple as the `claude-config` "Pipeline gates" exemption (G6-80-05). The author is the deciding fact; the branch name alone never exempts a PR. Only the two steps are skipped, so the required check `build-test` still reports, and the secret scan, build, tests and audits still run. Every other PR, including a fork PR or a human PR on any branch, is still linted.

Because the actor must also be Dependabot, anything that makes you the actor puts the PR back under commitlint, and it then fails on Dependabot's own commit:

- Do not push to a Dependabot branch.
- Do not close and reopen the PR, and do not use *Update branch*.
- Use `@dependabot rebase` (above) or `@dependabot recreate`; Dependabot then pushes as itself.

| Firefox (GitHub web UI) | CLI |
| --- | --- |
| Open the Dependabot PR, comment `@dependabot recreate` | `gh pr comment <number> --body "@dependabot recreate"` |

To change a bump by hand, close the PR and apply it on an `issue/<n>-bump-<pkg>` branch through the normal pipeline. Your own commits are linted by the local `commit-msg` hook and by CI.

### Ignored updates

Three updates are ignored in `.github/dependabot.yml` because the PR they open cannot merge:

| Ignored | Why | Lift it when |
| --- | --- | --- |
| `typescript` semver-major (npm, `/src/Decisya.Web`) | TypeScript 7 fails `npm ci` with ERESOLVE: the `typescript-eslint` peer range does not allow it (#106, #107) | `typescript-eslint` and `eslint-plugin-jsx-a11y` peer ranges accept the new major. Remove the rule and let Dependabot reopen the PR, or bump by hand on an `issue/<n>-bump-typescript` branch |
| `eslint` semver-major (npm, `/src/Decisya.Web`) | ESLint 10 fails `npm ci` for the same peer-range reason | Same condition as TypeScript |
| `keycloak/keycloak` (docker, `/.devcontainer/engine`, all versions and digests) | The image must change in `.devcontainer/engine/images.Dockerfile` and `src/Decisya.AppHost/ContainerImages.cs` in one PR (`ContainerImageParityTests`); a Dependabot bump touches only the Dockerfile (#105) | Never lift it. Bump by hand in one PR on an `issue/<n>-bump-keycloak` branch (`docs/runbooks/ci-security-gates.md`, "Image CVE scan") |

The Keycloak rule uses the name Dependabot shows in PR titles (`keycloak/keycloak`), not `quay.io/keycloak/keycloak`: Dependabot reports nothing when a rule matches nothing. The Postgres image in the same directory is not ignored. The weekly image CVE scan (ADR-0015) still scans the Keycloak reference in `ContainerImages.cs`, so the pinned image keeps a High/Critical signal.

### After merging #113 (Marco)

Dependabot cannot be exercised from a repository test, so check once after the merge and record the result in `docs/ai/pipeline/113.md`.

| Step | Firefox (GitHub web UI) | CLI |
| --- | --- | --- |
| 1. Make Dependabot re-read the file | *Insights → Dependency graph → Dependabot*, open the `/.devcontainer/engine` entry, choose *Check for updates* | no `gh` command; use the web UI **(unverified)** |
| 2. Read the log | The log must list `keycloak/keycloak` as ignored. Open the `/src/Decisya.Web` entry and check it shows no configuration error | same page **(unverified)** |
| 3. Close the old Keycloak PR (#105) and confirm no new one appears | Close #105, wait for the next run | `gh pr close 105`, then `gh pr list --author app/dependabot --search keycloak` **(unverified)** |
| 4. Rebase an open Dependabot PR and see `build-test` pass | Comment `@dependabot rebase` on it, open *Checks* | `gh pr comment <number> --body "@dependabot rebase"`, then `gh pr checks <number>` **(unverified)** |

A Dependabot PR with a long URL body line must now pass `build-test`; a non-Dependabot PR with a bad commit message must still fail it.

The `claude-config` job's "Pipeline gates" step exempts a Dependabot PR under the same three conditions (G6-80-05). A human pushing a commit to the same PR runs the step as that human and is not exempt, so it still needs a conforming manifest.

## Reverting a merged PR

GitHub's *Revert* button creates a branch named `revert-<pr>-<original-branch>`, which does not match `issue/<n>-<slug>` and so fails `claude-config`'s "Pipeline gates" step (G6-80-11). Open a GitHub issue for the revert first (as for any change), then redo the revert's diff by hand on `issue/<n>-revert-<slug>` instead of pushing the Revert button's own branch.

## The change trail

The ruleset's own History page records every change made to it (who, when, what changed) and is the direct audit trail for B2 (repository admin → ruleset) — it is read-only and not writable by CI or by an agent (G6-80-08).

| Visual Studio 2026 / GitHub web UI | CLI |
| --- | --- |
| *Settings → Rules → Rulesets → main*, open the **⋯** menu → *History* | `gh api repos/mpcs2013/decisya/rulesets/23835975/history` (read-only; Marco runs it) |

Check it after every apply and every break-glass episode, and whenever a live-state check (Verify, above) surprises you. The account-level security log (`github.com/settings/security-log`, filter `action:repository_ruleset`) is a second, independent source for the same events.

## See also

- `docs/runbooks/issue-pipeline.md` — the issue pipeline this ruleset now enforces (`claude-config`'s "Pipeline gates" step and the review-required paths it and G3/G6 cover).
- `docs/security/threat-models/main-ruleset.md` — the full threat model (T80-01 through T80-12) behind every step above.
