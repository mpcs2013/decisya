# Branch ruleset (main)

- Owner: devops · Last verified: 2026-09-27
- When to use: applying, changing or verifying the GitHub ruleset that protects `main` (id `23835975`), after `.github/rulesets/main.json` changes, at each phase exit, or when a required check is renamed, added or removed.
- Threat model: `docs/security/threat-models/main-ruleset.md`.

**Only Marco applies or changes the live ruleset, through the GitHub web UI or `gh api` under his own login. Agents never call the rulesets or branch-protection API** (`agent.gh-destructive` in `.claude/hooks/agent_boundaries.py` denies `gh api` to every agent session; this is a guardrail, not a boundary — see "What the hooks do" in `docs/runbooks/issue-pipeline.md`). Agents may edit `.github/rulesets/main.json` (the desired state, reviewed like any other file) and the drift test in `.claude/tests/`; they never PUT, PATCH or DELETE against `rulesets/*` or `rules/branches/*`, and never ask you to run such a command without showing you the diff first.

`main.json` mirrors the live ruleset except for one intentional addition: `changes` is a required context that is not yet required live (issue #80, G4-80-03 option (a)). Marco applies the file; from then on the two match exactly, less GitHub's read-only fields (`id`, `source`, `node_id`, `_links`, `created_at`, `updated_at`, `current_user_can_bypass`), which the file never carries.

## Apply

| Visual Studio 2026 / GitHub web UI | CLI |
| --- | --- |
| VS 2026 has no UI for rulesets; use the browser. On `github.com/mpcs2013/decisya`: *Settings → Rules → Rulesets → main*. Use *Import a ruleset* (or edit the existing one) and paste the contents of `.github/rulesets/main.json`, then *Save changes* | `gh api -X PUT repos/mpcs2013/decisya/rulesets/23835975 --input .github/rulesets/main.json` |

Read the file before applying it (`git show`, or open it in VS 2026's editor); the PUT is destructive to whatever the UI shows as current. Marco runs this step himself; an agent may hand him the exact command and the file diff, never run it.

## Verify (after every apply, and at each phase exit)

Three read-only checks (G4-80-11); none of them may use an Administration-scoped PAT, and none runs in CI.

| # | Check | Visual Studio 2026 / GitHub web UI | CLI | Expected |
| --- | --- | --- | --- | --- |
| 1 | Required checks | *Settings → Rules → Rulesets → main*, read the "Require status checks to pass" rule | `gh api repos/mpcs2013/decisya/rules/branches/main` | Lists `deletion`, `non_fast_forward`, `pull_request` and `required_status_checks`, and `required_status_checks` lists at least the floor set `build-test`, `claude-config`, `codeql`, `realm-guard` (plus `changes` once #80 is applied) |
| 2 | Enforcement and bypass | Same page, header shows "Active" and the "Bypass" list | `gh api repos/mpcs2013/decisya/rulesets/23835975` | `enforcement: "active"`, `bypass_actors: []` |
| 3 | A failing check blocks merge | Open a throwaway PR from a scratch branch that makes one required check fail (for example a syntax error caught by `build-test`), look at the merge box | `gh pr create` from the same scratch branch, then `gh pr view --json mergeStateStatus,statusCheckRollup` | The PR page (and `mergeStateStatus`) shows "Merging is blocked"; close the PR without merging and delete the branch afterwards |

Record the three outputs in the issue's G4 evidence (or, outside an issue, in a dated note in `docs/ai/` or the PR that changed the ruleset).

The file cannot see live state by itself (T80-07): these three checks are the manual control until a scheduled read-only comparison lands (`F-80-2`, tracked separately from #80).

## Renaming or removing a required check

A required context that stops reporting (renamed job, deleted job) is "Expected" forever and blocks every merge, including the fix — this fails closed, but do it in this order to avoid a self-inflicted lockout:

1. Add the new job/context to `ci.yml` and merge it — at this point both the old and the new context exist, and only the old one is required.
2. Once the new context has reported success on `main` at least once, update `.github/rulesets/main.json` to require the new context, and apply it (Apply, above).
3. Only then remove the old job from `ci.yml` and drop it from `main.json`'s floor set expectations. Removing a floor-set context (`build-test`, `claude-config`, `codeql`, `realm-guard`) also needs a change to the drift test's floor set, which makes the PR review-required (G4-80-02, G4-80-09).

Never remove the old context from `main.json` before the new one has a green run on `main`: doing so re-opens the T80-03 skip-through-`needs` gap for the gap between the two states.

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
| *Settings → Rules → Rulesets → main*, change "Enforcement status" to *Disabled*, save | `gh api -X PUT repos/mpcs2013/decisya/rulesets/23835975 --input <a copy of main.json with "enforcement": "disabled">` |

1. Set `enforcement` to `disabled` (above). Record the reason and the time in the GitHub issue you are unblocking.
2. Merge the fix (or the PR that needed to go through).
3. Set `enforcement` back to `active` the same way, using the checked-in `.github/rulesets/main.json` (which always has `enforcement: "active"`) so the live state matches the file again.
4. Re-run Verify (above) and record the three outputs.
5. Record the whole break-glass episode — start time, reason, who, end time, verify output — in the GitHub issue.

Only Marco performs a break-glass change; an agent never sets `enforcement` to anything, in a live call or in a suggestion meant to be run without review.

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

The `claude-config` job's "Pipeline gates" step recognizes a Dependabot PR by `github.event.pull_request.user.login == 'dependabot[bot]'` (not by branch name) and skips the manifest check, since Dependabot PRs carry no `docs/ai/pipeline/<n>.md`.

## The change trail

GitHub's security log records every ruleset change (who, when, what changed) and is the audit trail for B2 (repository admin → ruleset) — it is not writable by CI or by an agent.

| Visual Studio 2026 / GitHub web UI | CLI |
| --- | --- |
| `github.com/mpcs2013/decisya/settings/security-log`, filter `action:repository_ruleset` | `gh api repos/mpcs2013/decisya --jq .full_name` then open the same URL in a browser (the security log has no read API for a repository outside an enterprise/organization audit-log endpoint) |

Check it after every apply and every break-glass episode, and whenever a live-state check (Verify, above) surprises you.

## See also

- `docs/runbooks/issue-pipeline.md` — the issue pipeline this ruleset now enforces (`claude-config`'s "Pipeline gates" step and the review-required paths it and G3/G6 cover).
- `docs/security/threat-models/main-ruleset.md` — the full threat model (T80-01 through T80-12) behind every step above.
