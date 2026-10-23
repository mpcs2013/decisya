# CI security gates (pins, dependency canary, image scan, CodeQL)

- Owner: devops · Last verified: 2026-10-03 (Python 3.14.4, .NET SDK from `global.json`). The NuGet canary and `git ls-remote` commands were run on 2026-10-03. Commands that agents cannot run (`npm`, Docker, `gh api`, `pre-commit`) are marked **(unverified)**.
- "Locked restore and the private Ostium feed" was written 2026-10-17 (G4 step 6 of #164). The agent that wrote it could run only `python .claude/scripts/lint.py` and `actionlint`, so every command in it is marked **(unverified)** until Marco or G5 has run it.
- When to use: bumping or checking a pinned action or hook, renewing an image-scan exception, running the dependency canary by hand, reading a failed or disabled scheduled run, or explaining what a green check means.
- Design: `docs/architecture/ci-hardening.md`, ADR-0014 (pinning), ADR-0015 (image scan). Threat model: `docs/security/threat-models/ci-hardening.md`.

`ci.yml` is also reviewed as a whole before every merge (see "Read every workflow hunk before merging" in `docs/runbooks/main-ruleset.md`).

## What each gate proves

| Gate | Where | A green result means | It does not mean |
| --- | --- | --- | --- |
| Locked restore (`dotnet restore --locked-mode`, `DECISYA_LOCKED_RESTORE`) | every job that restores, `ci.yml` and `release.yml` | every package, `Ostium.*` included, has exactly the version and content hash in the committed `packages.lock.json`, from the one source `NuGet.config` allows for its id | that the hash in the lock file is the supplier's good one: that is the A3 check and Ostium's check script (ADR-0019) |
| Restore audit (`NuGetAudit`, warnings as errors) | `build-test`, step Restore | no package in the graph has a known advisory | that unknown vulnerabilities do not exist |
| Dependency canary (NuGet) | `build-test` | a seeded High advisory still fails the restore under the real repository props | nothing about the repository's own packages |
| `npm audit --audit-level=high`, `npm audit signatures` | `build-test`, SPA lane | no High or Critical advisory, and valid registry signatures | |
| Dependency canary (npm) | `build-test`, SPA lane | a seeded High advisory still fails `npm audit` | |
| `image-scan` | required job | the five pinned vendor images (Postgres, Keycloak, Redis, Caddy, the OTel collector) have no unexcepted High or Critical finding, **when the step ran** | the step is skipped on PRs that do not touch a scan input (see below) |
| `deploy-guards` | required job after Marco adds it to the ruleset (#120) | no home-network address in any tracked or unignored text file; a fresh AppHost publish equals `deploy/compose/docker-compose.yaml`; the merged Compose configuration, the Caddyfile (`caddy adapt` in the pinned image), `stackctl.py` and the exceptions file pass the D10 guards. It always runs: no `paths:` filter, no `needs`, no `if:`, and a missing Docker fails it. Run it locally with `python3 -m unittest discover -s deploy/tests -p "test_*.py" -v` **(unverified)** | that the running stack is healthy: `deploy/tests/stack_smoke.py` is run by hand, not in CI. See `docs/runbooks/deployable-stack.md`. |
| `apphost-tests` | required job after Marco adds it to the ruleset (#123) | the `Category=AppHost` tests passed against the real AppHost on the runner, **when the steps ran** | the steps are skipped on PRs outside the `fullstack` lane (see "Full-stack jobs") |
| `e2e` | required job after Marco adds it to the ruleset (#123) | the Playwright suite (Firefox and Chromium, axe) passed against the AppHost, and the artifact scan found no password or token, **when the steps ran** | same lane skip; dev mode only, so no Caddy headers, HSTS or production realm |
| `zap` | required job after Marco adds it to the ruleset (#123) | the passive ZAP baseline (traditional spider plus seeded endpoints) of the unauthenticated BFF surface has no Medium or High alert without an active exception, and the result passed our fail-closed checks, **when the steps ran** | an authenticated scan, an API scan or the production-shaped edge (#83); Low and Informational alerts are reported, never blocking |
| `codeql` | required job | the analysis ran and uploaded its results | **that there are no alerts**: alerts do not block merge (#83 tracks a `code_scanning` rule) |
| Pin guard `test_ci_pins.py` | `claude-config`, pre-push | every `uses:` and hook revision is a full SHA with a version comment | that the SHA really is the tag's commit (check it, below) |

## Check or bump a pinned action or hook

A pin is `uses: owner/repo@<40 hex>  # vX.Y.Z`. The guard checks the shape only. It reads the comment, not the commit, so a hand-edited or agent-edited pin whose SHA and comment disagree stays green (T28-13). Compare them on any pin PR that is not Dependabot's. A Dependabot pin needs only the PR diff read.

| # | Step | Visual Studio 2026 | CLI |
| --- | --- | --- | --- |
| 1 | Resolve the tag to its commit. Take the `^{}` line when it exists (an annotated tag), otherwise the single line | — (terminal only): *View → Terminal*, then the CLI command | `git ls-remote https://github.com/<owner>/<repo> refs/tags/<tag> 'refs/tags/<tag>^{}'` |
| 2 | Compare it with the SHA in the diff | *Git Changes*, open `.github/workflows/ci.yml` | `git diff origin/main...HEAD -- .github/workflows .pre-commit-config.yaml` |
| 3 | Confirm the SHA belongs to the upstream repository (an imposter commit exists only in a fork network, GHSA-69fq-xp46-6x23). Expect `identical` | — (terminal only) | `gh api repos/<owner>/<repo>/compare/<tag>...<sha> --jq .status` **(unverified)** |
| 4 | Read the action's `runs:` block at that SHA. A `composite` action's inner `uses:` and a `docker` action's `image:` must be pinned or listed as residuals in the PR body | Browser (Firefox): `https://github.com/<owner>/<repo>/blob/<sha>/action.yml` | `gh api repos/<owner>/<repo>/contents/action.yml?ref=<sha> --jq .content \| base64 -d` **(unverified)** |

Known residual: `wagoid/commitlint-github-action` is a Docker action whose image `docker://wagoid/commitlint-github-action:6.2.1` is a tag, not a digest. The two commitlint steps therefore run last in `build-test` (S-01). Replacing or digest-pinning it is in #83.

Dependabot (`github-actions`, `pre-commit`, `docker`, `nuget`, `npm`) opens the bump PRs weekly with a 7-day cooldown. The gitleaks version in `.pre-commit-config.yaml` (the `# frozen:` comment) must equal `GITLEAKS_VERSION` in `ci.yml` (`test_gitleaks_parity.py`), so a Dependabot gitleaks PR needs the second edit by hand in the same PR.

**Frozen fallback.** If the first `pre-commit` Dependabot run does not rewrite the `rev:` SHA together with its `# frozen:` comment, bump by hand at each phase exit:

| Visual Studio 2026 | CLI |
| --- | --- |
| — (terminal only): *View → Terminal*, then the CLI command | `pre-commit autoupdate --freeze` **(unverified)**, then edit `GITLEAKS_VERSION` in `ci.yml` to match |

Check after the first Dependabot run: *Insights → Dependency graph → Dependabot* (browser, Firefox) lists every entry with a recent "last checked" and no error. A rejected `cooldown` key would stop all updates for that entry.

## Run the dependency canary locally

The canary generates a vulnerable project at run time (NuGet `Newtonsoft.Json 12.0.3`, npm `lodash 4.17.20`), never builds or runs it, and passes only when the gate fails. It needs `dotnet` (NuGet) or `npm` (npm), and network access. Agents run the NuGet one. Only Marco runs the npm one (the `npm` install subcommand is blocked for agents on purpose).

| # | Step | Visual Studio 2026 | CLI |
| --- | --- | --- | --- |
| 1 | NuGet gate | *View → Terminal* (Developer PowerShell), then the CLI command | `python .github/scripts/dependency_canary.py nuget` |
| 2 | npm gate | *View → Terminal* | `python .github/scripts/dependency_canary.py npm` **(unverified)** |
| 3 | Show that the canary can turn red (local only) | *View → Terminal* | `python .github/scripts/dependency_canary.py nuget --weaken-for-evidence` |

## Verify

- Steps 1 and 2 print `OK: ...` and exit 0 (`echo $?` or `$LASTEXITCODE`).
- Step 3 must **fail** with `FAIL: restore succeeded, so the NuGet audit gate is open` and exit 1. That proves the canary notices an open gate. The option is refused under `GITHUB_ACTIONS=true` (exit 2) and never appears in `ci.yml`.
- `artifacts/dependency-canary/` does not exist afterwards. A stale one from a killed run is removed at the next start.

Observed on 2026-10-03: `error NU1903: Warning As Error: Package 'Newtonsoft.Json' 12.0.3 has a known high severity vulnerability, https://github.com/advisories/GHSA-5crp-9r3c-p9vr`, and with the option, `warning NU1903` plus the `FAIL` line above.

## Rollback

Nothing persists. If a run was killed, delete `artifacts/dependency-canary/` and, for the npm canary, the `decisya-npm-canary-*` folder in your temp directory.

## If a canary fails in CI

| Message | Meaning | Action |
| --- | --- | --- |
| `restore succeeded, so the NuGet audit gate is open` | someone weakened `NuGetAudit`, its level or `TreatWarningsAsErrors` | find the change in `Directory.Build.props` or the workflow; do not edit the canary |
| `proof was not made` with `DECISYA0009` in the output | the canary no longer passes both unlock switches (`-p:RestoreLockedMode=false` and `-p:DECISYA_LOCKED_RESTORE=false`), or `Directory.Build.targets` changed | restore both in `dependency_canary.py` (`UNLOCKED_RESTORE_ARGS`); see "The canary and locked mode" |
| `proof was not made` | restore failed for another reason (offline, `NU1301`, a `DECISYA000x` policy error) or the advisory feed was empty | read the printed output; re-run once if the feed was unreachable |
| `npm audit exited 0` | the npm gate is open, or the advisory data was unreachable | same |
| the seeded advisory withdrawn or downgraded | the registry or GitHub changed the rating | pick another High or Critical advisory in a new issue, never a lower one |

## Locked restore and the private Ostium feed

Design: `docs/architecture/ostium-jwt-switch.md` (D2), threat model `docs/security/threat-models/ostium-jwt-switch.md` (M-1, M-5, S-1, S-2, S-6), ADR-0019 (A5 key change and compromise, A6 restore hardening).

What is in place:

- `NuGet.config` (repository root) clears all sources and defines two: nuget.org for every id except `Ostium.*`, and the private feed (source key `ostium`) for `Ostium.*` only. It holds no credential.
- Every project has a committed `packages.lock.json`. `ci.yml` and `release.yml` set `DECISYA_LOCKED_RESTORE: "true"` once, at workflow level, so every implicit restore is locked. Every explicit `dotnet restore` also passes `--locked-mode`, which wins over anything a project sets. A project that turns locked mode off fails the build with `DECISYA0009`.
- No job that restores uses a package cache (no `cache:` on `setup-dotnet`, no `actions/cache`). Locked mode trusts a package that is already on disk, so a cache filled earlier would bypass the lock file.
- No dotnet command passes `--source`, `-s`, `--configfile` or `RestoreSources`.
- The feed credential is the job's own token, as the environment variable `NuGetPackageSourceCredentials_ostium`. It is set only as step-level `env:` on a `run:` step that restores: never on a `uses:` step, never at job or workflow level, never written to `$GITHUB_ENV`. The restore runs first, so later steps find every package in the global folder and need no credential.
- `packages: read` is granted only to the jobs that restore. `deploy/tests/test_ci_workflow.py` checks all of this.

| Workflow / job | `permissions` | Restore step that carries the credential |
| --- | --- | --- |
| `ci` / `build-test` | `contents: read`, `packages: read` | `Restore` (`dotnet restore --locked-mode`) |
| `ci` / `codeql` | `contents: read`, `security-events: write`, `packages: read` | `Restore` before `dotnet build --no-restore` |
| `ci` / `apphost-tests` | `contents: read`, `packages: read` | `Restore`, lane-gated, before the build |
| `ci` / `e2e`, `zap` | `contents: read`, `packages: read` | `Restore (AppHost)`, lane-gated, before the Release build |
| `ci` / `deploy-guards` | `contents: read`, `packages: read` | `Restore (AppHost)`, unconditional, before the drift check |
| `ci` / `realm-guard` | `contents: read` | none: `Decisya.Identity.Tests` has no `Ostium.*` in its graph |
| `ci` / `changes`, `claude-config`, `image-scan` | `contents: read` (the workflow default or the job's own block) | none: no dotnet |
| `release` / `build` | `contents: read`, `packages: read` | `Restore (Api, Ostium feed)`; the BFF and migrator restore without a credential |
| `release` / the other jobs | unchanged | none: no dotnet |

If `realm-guard` ever fails with `NU1301` or `401` on an `Ostium.*` id, `Decisya.Identity.Tests` gained an `Ostium.*` dependency. Do not give the job `packages: read`. Remove the dependency, or decide with Marco and change this table and the test together.

### Change a package version and its lock files

A package change rewrites the lock files, and they are committed in the same change. Locally the restore is unlocked on purpose.

| # | Step | Visual Studio 2026 | CLI |
| --- | --- | --- | --- |
| 1 | Change the version in `Directory.Packages.props` (an `Ostium.*` pin also needs the A3 check, ADR-0019, and Marco does it) | Edit the file, or *Tools → NuGet Package Manager → Manage NuGet Packages for Solution* | edit `Directory.Packages.props` |
| 2 | Restore unlocked, which rewrites the lock files | build the solution (*Build → Build Solution*) | `dotnet restore` **(unverified)** |
| 3 | Read the lock-file diff: only the intended ids and versions moved, and no `Ostium.*` hash moved unless the pin did | *Git Changes*, open each `packages.lock.json` | `git diff -- "**/packages.lock.json"` **(unverified)** |
| 4 | Prove the locked restore passes, as CI does | *View → Terminal* | `dotnet restore --locked-mode` **(unverified)** |
| 5 | Commit the lock files with the change | *Git Changes → Commit All* | `git add Directory.Packages.props "**/packages.lock.json"` then `git commit` **(unverified)** |

Run the locked restore on a new project too: a project without a lock file fails it (`NU1004`).

### NU1403 and NU1004 in CI

| Message | Meaning | Action |
| --- | --- | --- |
| `NU1403` on an id that starts with `Ostium.` | the package content does not match the hash in the lock file. **This is a compromise signal** (a republished version, a swapped package, a poisoned source). | Stop. Do not regenerate the lock files and do not re-run until it is green. Report to Marco, who handles it under ADR-0019 A5 (key change and compromise). A lock file regenerated to clear it would pin the bad package (G3 S-2). |
| `NU1403` on any other id | the same mismatch for a public package | Do not regenerate yet. Compare the package on nuget.org with the lock file's hash; report to Marco if you cannot explain it. |
| `NU1004` | a lock file is missing or out of date for the project or runtime (`linux-x64` for the release projects) | follow "Change a package version and its lock files" and commit the regenerated files; read the diff |
| `NU1301` or `401` on `nuget.pkg.github.com` | the job has no `packages: read`, the restore step lost its credential, or the token cannot read the three packages (package access for this repository is Marco's, O-006) | check the table above; report to Marco |
| `DECISYA0009` | a project or a step turned locked mode off while `DECISYA_LOCKED_RESTORE` is true | remove the override; the only exemption is the canary (below) |

### The canary and locked mode

`dependency_canary.py nuget` restores a generated project that has no lock file, and the property under test is the audit, not locked mode. It therefore passes both `-p:RestoreLockedMode=false` and `-p:DECISYA_LOCKED_RESTORE=false`: the first switches off locked mode, the second keeps `DECISYA0009` from objecting. The lock file the restore writes lands inside `artifacts/dependency-canary/`, which the script deletes. These two strings appear nowhere else in the repository. The canary's project maps `Newtonsoft.Json` to nuget.org only, so it needs no feed credential and the step has none.

### Fork pull requests

A fork PR may be able to restore the compiled `Ostium.*` packages: GitHub documents that forks may get access, it does not promise a refusal. Marco accepted this residual (exchange A-003, point 4); Ostium's source stays private. Set the repository to require approval before a fork's workflows run. Only Marco changes repository settings; an agent never calls the settings API.

| Visual Studio 2026 | CLI |
| --- | --- |
| browser (Firefox): *Settings → Actions → General → Fork pull request workflows from outside collaborators → Require approval for all external contributors → Save* | `gh api --method PUT repos/<owner>/<repo>/actions/permissions/fork-pr-contributor-approval -f approval_policy=all_external_contributors` **(unverified)**, Marco only |

A version change in a PR that is not on Marco's own branch is redone on his branch (ADR-0019 A3).

### Dependabot and the private feed

Dependabot reaches a private feed only through a `registries` entry whose `token` is a Dependabot secret holding a personal access token. The repository's `GITHUB_TOKEN` is not available to it. ADR-0019 and the threat model (S-6) forbid adding a PAT without Marco's ADR change, so `dependabot.yml` declares no registry and the `nuget` entry carries `ignore: Ostium.*`. Ostium bumps are made by hand under the A3 check and are never auto-merged.

Two things were not verified statically and are checked on the first Dependabot NuGet run (*Insights → Dependency graph → Dependabot*, browser, Firefox):

- whether the updater can resolve the projects whose graph includes `Ostium.*` (`Decisya.Api` and what references it) without the feed. If it cannot, those NuGet updates fail until Marco accepts the gap or changes ADR-0019;
- whether Dependabot rewrites `packages.lock.json`. If it does not, every Dependabot NuGet PR fails the locked restore with `NU1004`; redo it by hand on an `issue/<n>-bump-<package>` branch, as for a digest bump (`docs/runbooks/main-ruleset.md`, "Dependabot under a strict ruleset"), and keep the lock files in the same commit.

## Image CVE scan

`.github/scripts/image_scan.py` scans the five images in `src/Decisya.AppHost/ContainerImages.cs` (the only source; aliases `postgres`, `keycloak`, `redis`, `caddy` and `otelcollector` since #120) with the digest-pinned Grype image named in `.github/image-scan/Dockerfile` (never built). High and Critical findings fail, fixed or not, unless an unexpired exception covers them. A severity of `Unknown` or `Negligible` with a CVSS base score of 7.0 or higher counts as High.

The scan step runs when one of these changes (the `images` lane), on `workflow_dispatch`, and on the weekly `schedule`: `ContainerImages.cs`, `.devcontainer/engine/images.Dockerfile`, `.github/image-scan/**`, `image_scan.py`, `ci.yml`. On every other PR the step is skipped and the job reports success, by design.

| # | Step | Visual Studio 2026 | CLI |
| --- | --- | --- | --- |
| 1 | Run the scan once for real | browser (Firefox): *Actions → CI → Run workflow*, branch `main` | `gh workflow run CI --ref main`, then `gh run watch` **(unverified)** |
| 2 | Run it on your machine (needs Docker running; the script calls `docker run` and never mounts the socket) | *View → Terminal* | `python .github/scripts/image_scan.py` **(unverified)** |
| 3 | Read the findings | the run page: the *image-scan* step log and the job summary table | same page, or `gh run view <run-id> --log` **(unverified)** |

A new finding on a vendor image:

1. Prefer fixing it: bump the digest in `ContainerImages.cs` and `images.Dockerfile` together (`ContainerImageParityTests` keeps them equal). Dependabot no longer opens the Keycloak bump PR (ignored in `dependabot.yml`, #113), so that one is always a by-hand change. For any other digest a Dependabot bump cannot go green alone, so close it and redo it by hand on an `issue/<n>-bump-<image>` branch (the route in `docs/runbooks/main-ruleset.md`, "Dependabot under a strict ruleset").
2. If no fix exists, add an exception in `.github/image-scan/exceptions.json` through a PR on an issue. Review-required path: G3 and G6 run.

### Add or renew an exception

```json
{
  "id": "CVE-2026-12345",
  "image": "postgres",
  "package": "libexample",
  "justification": "Not reachable: the library is only loaded by the unused foo tool.",
  "issue": "#123",
  "added": "2026-10-03",
  "expires": "2026-12-01"
}
```

Rules, all checked before scanning (exit 2 otherwise): the id is a CVE or GHSA id; `image` is `postgres`, `keycloak` or `redis`; `package` is an exact name (no `*`, `?`, brackets or other pattern characters); `justification` has at least 20 characters; `issue` is `#<n>`; the dates are `YYYY-MM-DD`; `added` is not in the future; `expires` is not before `added` and at most 90 days after it; no entry repeats the same image, package and id. Matching is exact on image, package and id (or a related id in the scanner output). An expired entry is ignored, so the scan fails again, and the log lists it as `expired (not applied)`. An entry that matched nothing is listed as `stale`: remove it.

Renew by **replacing** the old entry (same image, package and id) with one that has a new `added` (today), a new `expires` (at most 90 days later), a fresh justification and the issue that approved the renewal. Do not add a second entry next to it: the validator rejects the duplicate and exits 2. The 90-day limit is the review cycle, so never stretch `expires` past it.

## Full-stack jobs

Design: `docs/architecture/full-stack-ci.md`, ADR-0020. Threat model: `docs/security/threat-models/full-stack-ci.md`. The full-stack run was verified in CI run 37975670109 (2026-10-10, spike 4: 12 URLs, all on the BFF origin). Commands marked **(unverified)** in this section are ones agents cannot run (Docker, `npm`, `gh run`, the Aspire stack); replace each mark with its date when Marco has run it.

Three jobs in `ci.yml` start the AppHost (run mode, ephemeral containers) on the runner:

| Job | What it runs | Failure artifact |
| --- | --- | --- |
| `apphost-tests` | `dotnet build -warnaserror`, then the `Category=AppHost` tests through `fullstack.py apphost-tests` | none |
| `e2e` | SPA build, Playwright (Firefox and Chromium, axe) through `fullstack.py e2e` | `e2e-results`, only on failure |
| `zap` | pinned ZAP baseline through `fullstack.py zap`, then the verdict from `zap_policy.py` | `zap-report`, always (after the scan) |

The `zap` job runs the traditional spider plus the seeded endpoints. The AJAX spider was dropped after spike 3 because it left the BFF scope (it followed `/bff/login` into Keycloak); binding it is in #83.

Rules that hold for all three:

- Each has `needs: changes` only and no job-level `if`. Every step after checkout runs only when the `fullstack` lane is true, so on a PR outside the lane the job reports success after the lane echo. **A green check on such a PR proves nothing about the stack.** Open the job: real steps show a green check, skipped ones a grey dash.
- The lane covers code, tests, `ci.yml`, `fullstack.py`, `zap_policy.py`, `spa_package_guard.py` and `.github/zap/`. It is always true on `schedule`, `workflow_dispatch` and a push to `main`.
- The dev password is generated per run, masked first, and lives only in the child processes. It is not a GitHub secret (ADR-0020). Nothing is uploaded before the scan for the password and JWT shapes passed; a hit deletes the directory and fails the step.
- Artifacts are not uploaded for fork PRs and are kept 7 days. The repository is public: treat them as public.

| # | Step | Visual Studio 2026 | CLI |
| --- | --- | --- | --- |
| 1 | Run all three for real on a branch | browser (Firefox): *Actions → CI → Run workflow*, pick the branch | `gh workflow run CI --ref <branch>`, then `gh run watch` **(unverified)** |
| 2 | Check that the steps ran, not only the lane echo | the run page: open each job, look for grey dashes | `gh run view <run-id> --json jobs --jq '.jobs[] \| {name, steps: [.steps[] \| {name, conclusion}]}'` **(unverified)** |
| 3 | Run the AppHost tests on your machine (Docker running) | *Test Explorer*, trait `Category=AppHost` | `dotnet test --project tests/Decisya.AppHost.Tests --filter-trait "Category=AppHost"` **(unverified)** |
| 4 | Run the verdict script on a saved report | *View → Terminal* | `python .github/scripts/zap_policy.py --report <report.json> --urls <urls file> --zap-exit 0` **(unverified)** |

### Reading a red `zap`

1. Read the job summary table ("ZAP baseline") or download `report.json` from the `zap-report` artifact and read it in an editor. **Open `report.html` only from runs Marco started** (a workflow_dispatch run or his own branch): it renders text from the scanned app, and a fork or Dependabot run is not his.
2. Find the cause in the table:
   - `FAIL` rows: a Medium or High alert without an active exception. Fix the cause in the BFF or the SPA.
   - `RESULT NOT TRUSTED`: ZAP exited with another code than 0, 1 or 2, the report or the URL list is missing, the report names another site, or the URL list lacks `/` or `/bff/me`. The stack probably did not start; read the `fullstack.py` output, which drops the dashboard login-token lines.
   - `expired (not applied)`: an exception ran out. Renew or fix (below).
3. Fix the cause, or add an expiring exception through a PR with an issue. **Never bypass the ruleset** to get past a red `zap`.
4. **Never change a threshold** (which risk fails, the confidence rule, the required URLs, the site check, the 90-day limit) without G3: that is a security change, and `zap_policy.py` is a review-required path.
5. A flake (the stack timed out, a download failed): re-run the job once. If it repeats, file an issue. Do not retry in a loop, and do not use break-glass for a flaky check (`docs/runbooks/main-ruleset.md`).

| # | Step | Visual Studio 2026 | CLI |
| --- | --- | --- | --- |
| 1 | Open the summary | browser (Firefox): the run page, "Summary" | `gh run view <run-id>` **(unverified)** |
| 2 | Download the report | the run page, *Artifacts → zap-report* | `gh run download <run-id> --name zap-report --dir <scratch directory outside the repo>` **(unverified)** |
| 3 | Read `report.json` | open it in VS 2026 (*File → Open → File*) | `python -m json.tool <scratch directory>/report.json` **(unverified)** |

### Add or renew a ZAP exception

Only Medium and High alerts need one. Add it in `.github/zap/exceptions.json` through a PR on an issue. Review-required path: G3 and G6 run.

```json
{
  "pluginId": "10038",
  "path": "/bff/",
  "justification": "Not exploitable here: the response is JSON with no HTML context.",
  "issue": "#123",
  "added": "2026-10-09",
  "expires": "2026-12-01"
}
```

Rules, all checked before the report is read (exit 2 otherwise): the keys are exactly `pluginId`, `path`, `justification`, `issue`, `added`, `expires`, all strings; `pluginId` is digits; `path` is an exact path, or a prefix that ends in `/` and is not `/` alone, with no query, glob or whitespace; `justification` has at least 20 characters; `issue` is `#<n>`; the dates are `YYYY-MM-DD`; `added` is not in the future; `expires` is not before `added` and at most 90 days after it; no entry repeats the same `pluginId` and `path`. Matching is exact on `pluginId` and on the parsed path of each alert instance (the query is ignored). An expired entry is ignored, so the job fails again, and the summary lists it as `expired (not applied)`. An entry that matched nothing is listed as `stale`: remove it.

Renew by **replacing** the old entry with one that has `added` today, `expires` at most 90 days later, a fresh justification and the issue that approved the renewal. Never add a second entry next to it, and never stretch `expires` past 90 days.

| # | Step | Visual Studio 2026 | CLI |
| --- | --- | --- | --- |
| 1 | File an issue for the exception | browser (Firefox): *Issues → New issue* | `gh issue create` **(unverified)** |
| 2 | Edit the file on `issue/<n>-<slug>` | open `.github/zap/exceptions.json` in the editor | same file, any editor |
| 3 | Validate it (the schema is checked first, before the report is read; the offline tests are `.claude/tests/test_zap_policy.py` once G4 part 2 writes them) | *View → Terminal* | `python .github/scripts/zap_policy.py --report <saved report.json> --urls <saved urls file> --zap-exit 0` **(unverified)** |
| 4 | Push and read the `zap` summary: the entry shows under "Excepted" and not under "stale" | the run page | `gh run view <run-id>` **(unverified)** |

The scanner image pin is `.github/zap/Dockerfile` (never built). Dependabot bumps it weekly with a 7-day cooldown; ZAP is a CI tool and is not scanned by `image-scan` (ADR-0020).

## The weekly schedule

`schedule` runs `ci.yml` every Monday at 05:17 UTC on the default branch, with every lane (`changes` yields `ALL`), so it also catches advisories published since the last push. `codeql` skips on it. A failure notifies the person who last edited the cron line (Marco). It blocks no PR.

GitHub disables scheduled workflows in a public repository after 60 days without repository activity. If the weekly run stops appearing:

| Visual Studio 2026 | CLI |
| --- | --- |
| browser (Firefox): *Actions → CI*, use the banner "This scheduled workflow is disabled" → *Enable workflow* | `gh workflow enable CI` **(unverified)** |

## Where the old gaps went

- `Vulnerable packages` step: `defaults: run: shell: bash` gives `-eo pipefail`, so a failing `dotnet list` can no longer be hidden by `tee`. A test requires the default.
- Tokens: top-level `contents: read`; `codeql` adds `security-events: write`; the jobs that restore add `packages: read` (see "Locked restore and the private Ostium feed"); no checkout keeps credentials.
- ZAP, AppHost tests in CI and Playwright in CI: closed by #123 (jobs `zap`, `apphost-tests`, `e2e`; see "Full-stack jobs"). What stays open goes to #83: a production-shaped ZAP baseline against the Compose stack, the authenticated scan, the API scan once an OpenAPI document exists, and browser and Aspire CLI checksums.
