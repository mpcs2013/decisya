# CI security gates (pins, dependency canary, image scan, CodeQL)

- Owner: devops · Last verified: 2026-10-03 (Python 3.14.4, .NET SDK from `global.json`). The NuGet canary and `git ls-remote` commands were run on 2026-10-03. Commands that agents cannot run (`npm`, Docker, `gh api`, `pre-commit`) are marked **(unverified)**.
- When to use: bumping or checking a pinned action or hook, renewing an image-scan exception, running the dependency canary by hand, reading a failed or disabled scheduled run, or explaining what a green check means.
- Design: `docs/architecture/ci-hardening.md`, ADR-0014 (pinning), ADR-0015 (image scan). Threat model: `docs/security/threat-models/ci-hardening.md`.

`ci.yml` is also reviewed as a whole before every merge (see "Read every workflow hunk before merging" in `docs/runbooks/main-ruleset.md`).

## What each gate proves

| Gate | Where | A green result means | It does not mean |
| --- | --- | --- | --- |
| Restore audit (`NuGetAudit`, warnings as errors) | `build-test`, step Restore | no package in the graph has a known advisory | that unknown vulnerabilities do not exist |
| Dependency canary (NuGet) | `build-test` | a seeded High advisory still fails the restore under the real repository props | nothing about the repository's own packages |
| `npm audit --audit-level=high`, `npm audit signatures` | `build-test`, SPA lane | no High or Critical advisory, and valid registry signatures | |
| Dependency canary (npm) | `build-test`, SPA lane | a seeded High advisory still fails `npm audit` | |
| `image-scan` | required job | the three pinned vendor images have no unexcepted High or Critical finding, **when the step ran** | the step is skipped on PRs that do not touch a scan input (see below) |
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
| `proof was not made` | restore failed for another reason (offline, `NU1301`, a `DECISYA000x` policy error) or the advisory feed was empty | read the printed output; re-run once if the feed was unreachable |
| `npm audit exited 0` | the npm gate is open, or the advisory data was unreachable | same |
| the seeded advisory withdrawn or downgraded | the registry or GitHub changed the rating | pick another High or Critical advisory in a new issue, never a lower one |

## Image CVE scan

`.github/scripts/image_scan.py` scans the three images in `src/Decisya.AppHost/ContainerImages.cs` (the only source) with the digest-pinned Grype image named in `.github/image-scan/Dockerfile` (never built). High and Critical findings fail, fixed or not, unless an unexpired exception covers them. A severity of `Unknown` or `Negligible` with a CVSS base score of 7.0 or higher counts as High.

The scan step runs when one of these changes (the `images` lane), on `workflow_dispatch`, and on the weekly `schedule`: `ContainerImages.cs`, `.devcontainer/engine/images.Dockerfile`, `.github/image-scan/**`, `image_scan.py`, `ci.yml`. On every other PR the step is skipped and the job reports success, by design.

| # | Step | Visual Studio 2026 | CLI |
| --- | --- | --- | --- |
| 1 | Run the scan once for real | browser (Firefox): *Actions → CI → Run workflow*, branch `main` | `gh workflow run CI --ref main`, then `gh run watch` **(unverified)** |
| 2 | Run it on your machine (needs Docker running; the script calls `docker run` and never mounts the socket) | *View → Terminal* | `python .github/scripts/image_scan.py` **(unverified)** |
| 3 | Read the findings | the run page: the *image-scan* step log and the job summary table | same page, or `gh run view <run-id> --log` **(unverified)** |

A new finding on a vendor image:

1. Prefer fixing it: bump the digest in `ContainerImages.cs` and `images.Dockerfile` together (`ContainerImageParityTests` keeps them equal). A Dependabot digest bump cannot go green alone, so close it and redo it by hand on an `issue/<n>-bump-<image>` branch (the route in `docs/runbooks/main-ruleset.md`, "Dependabot under a strict ruleset").
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

## The weekly schedule

`schedule` runs `ci.yml` every Monday at 05:17 UTC on the default branch, with every lane (`changes` yields `ALL`), so it also catches advisories published since the last push. `codeql` skips on it. A failure notifies the person who last edited the cron line (Marco). It blocks no PR.

GitHub disables scheduled workflows in a public repository after 60 days without repository activity. If the weekly run stops appearing:

| Visual Studio 2026 | CLI |
| --- | --- |
| browser (Firefox): *Actions → CI*, use the banner "This scheduled workflow is disabled" → *Enable workflow* | `gh workflow enable CI` **(unverified)** |

## Where the old gaps went

- `Vulnerable packages` step: `defaults: run: shell: bash` gives `-eo pipefail`, so a failing `dotnet list` can no longer be hidden by `tee`. A test requires the default.
- Tokens: top-level `contents: read`; `codeql` adds `security-events: write`; no checkout keeps credentials.
- ZAP, AppHost tests in CI and Playwright in CI moved to #29.
