# Branch ruleset (main)

- Owner: devops · Last verified: 2026-09-27
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
| 1 | Required checks | *Settings → Rules → Rulesets → main*, read the "Require status checks to pass" rule | `gh api repos/mpcs2013/decisya/rules/branches/main` | Lists `deletion`, `non_fast_forward`, `pull_request` and `required_status_checks`, and `required_status_checks` lists at least the floor set `build-test`, `claude-config`, `codeql`, `realm-guard` (plus `changes` once #80 is applied — five contexts total) |
| 2 | Enforcement and bypass | Same page, header shows "Active" and the "Bypass" list | `gh api repos/mpcs2013/decisya/rulesets/23835975` | `enforcement: "active"`, `bypass_actors: []` |
| 3 | A failing check blocks merge | Open a throwaway PR from a scratch branch that makes one required check fail (for example a syntax error caught by `build-test`), look at the merge box | `gh pr create` from the same scratch branch, then `gh pr view --json mergeStateStatus,statusCheckRollup` | The PR page (and `mergeStateStatus`) shows "Merging is blocked"; close the PR without merging and delete the branch afterwards |
| 4 | Default workflow token permissions | *Settings → Actions → General → Workflow permissions*, read the selected option | `gh api repos/mpcs2013/decisya/actions/permissions/workflow` | "Read repository contents and packages permissions" is selected; `default_workflow_permissions: "read"`. This is what a workflow file with no top-level `permissions:` key would inherit (G6-80-03); `ci.yml` and `claude-review.yml` both declare their own narrower `permissions:` today regardless |

Record the four outputs in the issue's G4 evidence (or, outside an issue, in a dated note in `docs/ai/` or the PR that changed the ruleset).

The file cannot see live state by itself (T80-07): these four checks are the manual control until a scheduled read-only comparison lands (`F-80-2`, tracked separately from #80).

## Renaming or removing a required check

A required context that stops reporting (renamed job, deleted job) is "Expected" forever and blocks every merge, including the fix — this fails closed, but do it in this order to avoid a self-inflicted lockout:

1. Add the new job/context to `ci.yml` and merge it — at this point both the old and the new context exist, and only the old one is required.
2. Once the new context has reported success on `main` at least once, update `.github/rulesets/main.json` to require the new context, and apply it (Apply, above).
3. Update `.github/rulesets/main.json` again to drop the old context, and apply that file too — from `origin/main` or a reviewed PR head (Apply, above) — **before** merging the PR below. Removing a floor-set context (`build-test`, `claude-config`, `codeql`, `realm-guard`) also needs a change to the drift test's floor set, which makes the PR review-required (G4-80-02, G4-80-09).
4. Only once the live ruleset no longer requires the old context, merge the PR that removes the old job from `ci.yml`.

Never merge the PR that removes the old job while the live ruleset still requires its context: the context stops reporting and shows "Expected" forever, which blocks every merge, including the fix (T80-01) — it does not skip any check, it locks the repository out of merging.

## `codeql` is skip-safe

`codeql` is required but skips (reports success) on a private repository, and on any PR that touches neither .NET nor SPA code (`needs.changes` outputs both `false`). This is intentional and documented in the drift test's skip-safe map, not a bug. When GitHub Advanced Security is enabled for the repository, or the job is removed, do it together with issue #28 (0.16, CI hardening, `F-80-1`) and in the same PR:

- update `.github/rulesets/main.json` if the context's behavior or name changes;
- update the skip-safe map and floor set in the drift test;
- update this runbook's note.

Until then, `codeql` showing green on a private-repo PR proves nothing about that PR; do not read it as a security signal.

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

A narrower alternative to disabling enforcement entirely: add a temporary bypass actor limited to Marco's own account, remove it once the merge has gone through, and re-run Verify check 2 to confirm `bypass_actors: []` again.

Only Marco performs a break-glass change; an agent never sets `enforcement` or `bypass_actors` to anything, in a live call or in a suggestion meant to be run without review.

## Read every workflow hunk before merging

The ruleset proves a check named `build-test`, `realm-guard`, `claude-config` or `codeql` passed — it cannot tell "the check ran `main`'s definition of that job" apart from "the PR edited the job and its own edited version passed" (T80-05: every Actions workflow reports under the same GitHub App, so `integration_id` pinning does not separate a same-repo edit from the real job). Before merging any PR that touches `.github/workflows/**` or `.github/rulesets/**`:

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

Group headers stay short: `chore(deps): bump the opentelemetry group with 5 updates` is 56 characters. A single long NuGet name can still exceed commitlint's 100-character limit. Checked with the local mirror on 2026-09-27: `chore(deps): bump OpenTelemetry.Instrumentation.EntityFrameworkCore from 1.12.0-beta.2 to 1.13.0-beta.1` is 103 characters and fails.

`build-test`'s commitlint step checks Dependabot's commit, not the PR title, so editing the title doesn't help. The fix is to add the package to a group in `dependabot.yml` (its grouped header is short), merge that, then recreate the PR:

| VS 2026 / GitHub web UI | CLI |
| --- | --- |
| Open the Dependabot PR, comment `@dependabot recreate` | `gh pr comment <number> --body "@dependabot recreate"` |

A grouped, short header does not always save the commit: Dependabot's commit body also counts against commitlint. Each commit carries `Bumps [<package>](<url>) from <old> to <new>.` and one or more `- [Commits](<compare-url>)` lines, and commitlint's `body-max-line-length` (100 characters) applies to each of them — a long package name or a long tag name in the compare URL can push a body line over the limit even though the header is short, and a grouped PR repeats the pattern once per bumped package. When that happens, `build-test`'s commitlint step fails on the body, not the header, and regrouping the header does not fix it (the grouped commit still carries the same long body lines). There is no automatic exemption for this case; Marco decides: read the failing line and, if only the body is over budget and nothing else is wrong, merge anyway (through break-glass, above, since commitlint is a required check); or ask for a different, narrower grouping in `dependabot.yml` so that package's line is not generated in that group; or add a temporary `ignore` entry for that dependency until it can be regrouped.

The `claude-config` job's "Pipeline gates" step exempts a Dependabot PR only when three things all hold: `github.event.pull_request.user.login`, `github.actor` and the head branch (`github.head_ref`, must start with `dependabot/`) are all Dependabot's (G6-80-05). A human pushing a commit to the same PR runs the step as that human and is not exempt, so it still needs a conforming manifest.

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
