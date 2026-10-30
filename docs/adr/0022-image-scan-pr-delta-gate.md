# 0022. Gate a pull request on its own image changes; track upstream drift on a schedule with fix deadlines; keep the strict gate at release and deploy

- Status: Accepted (Marco, 2026-10-30)
- Date: 2026-10-30
- Deciders: Marco
- Tags: security, tooling, hosting
- Issue: #216. Architecture note: [adr-0022-pr-gate-delta.md](../architecture/adr-0022-pr-gate-delta.md). Threat delta (G3): `docs/security/threat-models/adr-0022-pr-gate-delta.md`.
- Revises, for the pull-request path only:
  - [ADR-0015](0015-container-image-cve-scan-grype.md) decision item 3 (policy: any High or Critical fails, whether or not a fix exists);
  - ADR-0015 decision item 5 (when it runs: the `image-scan` step on images-lane changes, `ci.yml:81`; the weekly `schedule`, `ci.yml:9-10`).
- Unchanged, and still in force for release and deploy:
  - ADR-0015 decision items 1, 2 and 4;
  - the ADR-0015 amendment 2026-10-06 (item 1 binary-only images, item 2 the one-off waiver, item 3 fixed OS packages) and its D-2 rule (an upstream fix ends an exception at once);
  - the ADR-0015 amendment 2026-10-17 (item 4, Caddy `go-module`, sunset 2026-11-30), for as long as it exists ([ADR-0021](0021-self-built-caddy-edge-image.md) E1 retires it with #188 PR-2);
  - ADR-0021 (the strict build scan of the self-built Caddy image in `caddy-image.yml`).
- On acceptance, ADR-0015 and ADR-0021 (item 8, last bullet: "The weekly `image-scan` keeps scanning the pinned `caddy`") each get a dated pointer line to this ADR: the ADR-0015 pointer is added with #216, the ADR-0021 pointer with the implementation issue. Their decisions are not otherwise rewritten. ADR-0015 applies unchanged while this ADR is Proposed, and after acceptance until the implementation PR merges (G3 M-5).
- The implementation conditions M-2 to M-5 of the G3 delta (`docs/security/threat-models/adr-0022-pr-gate-delta.md`) are the "Done when" conditions of the implementation issue; they are not restated here.

## Context and problem statement

ADR-0015 makes `image-scan` a required check. On a pull request its step runs when the change touches a file of the images lane (`ci.yml:81`):
- `src/Decisya.AppHost/ContainerImages.cs` or `.devcontainer/engine/images.Dockerfile` (the pins and their parity file);
- `.github/image-scan/**` (the scanner pin `Dockerfile` and `exceptions.json`);
- `.github/release/**` (the release base and tool pins);
- `.github/scripts/image_scan.py` or `.github/workflows/ci.yml`.

It then scans all five pinned aliases (`postgres`, `keycloak`, `redis`, `caddy`, `otelcollector`) and fails on any High or Critical in any of them "whether or not a fix exists" (decision item 3). The scanner reads a live advisory database, so a pull request fails on findings that were published after it was opened and that its change did not cause.

Evidence:
- 1 to 24 October: 38 of 90 failed pull-request jobs were upstream drift (N-004/N-005 cause (e)).
- 23 to 30 October: Keycloak (twice), Postgres (twice), Caddy (three upstream rebuilds that fixed nothing), curl, brotli, guava and zod each blocked unrelated work (#190, #192, #195, #187, #212). Each needed an issue, a threat delta, Marco's approvals, exceptions with expiry dates, and often a cooldown waiver; several were invalidated days later by the next upstream rebuild.
- On 2026-10-30 the images lane became unmergeable by policy: Keycloak bundles guava with a fix (GHSA-xxph-c9ww-hj94, fixed in guava 33.7.2) that no Keycloak release carries, and D-2 allows no exception for a fixed `java-archive` finding. The chain #213 → #214 → #215 is paused.
- Nothing is deployed. The findings blocked development, not exposure.

The question: how do we keep the property "no known High or Critical reaches a running stack (any image) or a published image of ours (release, `caddy-image.yml`) without a recorded decision" while no longer making every pull request answer for the world's advisory feed?

## Decision drivers

- What runs (a deploy) and what is published (a release) must stay strictly gated.
- A pull request should fail for what it changes, not for news about images it does not touch.
- Drift must stay visible, owned and time-boxed, not silently tolerated.
- Fewer exceptions written under deadline pressure (each one costs review and expires).
- No new third-party action or token beyond what the workflow already uses.

## Considered options

1. **Status quo** (ADR-0015 as amended): every images-lane pull request answers for all current findings.
2. **Gate the pull request on its own changes (delta), track drift on a schedule with fix deadlines, keep release and deploy strict.**
3. **Status quo plus wider exception rules** (a new amendment item for vendor-bundled libraries with a fix but no vendor release), keeping every pull request on the full gate.
4. **Advisory-only scanning** (no blocking anywhere).

## Decision outcome

Chosen option: **2**.

1. **Pull-request gate = delta.** The pull request's `image-scan` scans each alias the pull request changes (its pin in `ContainerImages.cs`, or its line in the parity file) at the base and at the head, with the same scanner and the same database, and fails only on a High or Critical finding (package, id) present at the head and absent at the base. Unchanged images are not gated on a pull request. A change that only edits `exceptions.json` runs the validator, not the scan.
   - **Gate-change comparison** (Marco, 2026-10-30, "option 1"). A pull request that changes the scan code (`.github/scripts/image_scan.py`), the scanner pin (`.github/image-scan/Dockerfile`) or `ci.yml` changes the gate itself. It scans every pinned alias at the base and at the head and fails on any High or Critical finding (alias, package, id; the exact key of G3 M-2) present at the head and absent at the base.
     - The base is scanned with the base's scan code, scanner pin and policy (from `main`), the head with the pull request's. Both use the same database snapshot.
     - A failed, missing or untrusted base or head scan fails closed: every head finding counts as new.
     - A head that reports fewer findings than the base is logged. That alone is not a reason to pass, and it does not fail the comparison. Review (review-required paths, G6) stays the control against a scanner that under-reports, as it is today under the full scan.
     - No file-name heuristics beyond the existing images-lane path list (`ci.yml:81`).
2. **Drift = scheduled scan on `main`, daily** (today weekly, `ci.yml:9-10`), a full scan of every pinned image with the current policy. A red run opens or updates one tracking issue ("image drift") listing each finding with its first-seen date. Fix deadlines from first seen: **Critical 7 days, High 30 days**; on the request path of a LAN-reachable service (S-120-07) **High 14 days**. A finding is closed by a pin bump, an approved exception, or a recorded "not affected" statement (reachability, as today's class (a)/(b) arguments). A deadline is time to fix, not a grace period: every High or Critical without an active exception blocks `stackctl up` (item 4) whatever its age, from the day it is first seen. "Overdue" adds a notification (an OVERDUE marker, the issue assigned to Marco, a red scheduled run), not a new block, and never a pull-request blocker.
   - **Release blocker for third-party images = the deploy gate** (G2 Q1 option (c), G3 M-1). A third-party image reaches a running stack only through `stackctl`, so item 4 is where it is blocked. `release.yml` and `caddy-image.yml` stay strict for our own images (item 3). No release-time drift check is added, and the tracking issue is never gate input: nothing gates on its contents or its first-seen dates.
   - **"Not affected" counts only as an exception.** The deploy gate honours only `exceptions.json`, so a "not affected" statement lets a deploy through only as a valid exception entry, and D-2 with its two narrowings (amendment items 3 and 4) still voids it for a fixed finding. Consequence (the guava case): Keycloak's bundled guava with GHSA-xxph-c9ww-hj94 is a fixed `java-archive` finding, so `stackctl up` refuses until a Keycloak release carries guava 33.7.2 or later. Any way round D-2 needs a new ADR-0015 amendment with its own G3, never this ADR.
3. **Release stays strict, unchanged:** the release workflow's strict D-2 scan of our own images (`api`, `bff`, `migrator`; `release_images.py`, `evaluate()` without `ref`) and the self-built Caddy build scan (ADR-0021, `caddy-image.yml`) still block on any High or Critical without an approved exception.
4. **Deploy stays strict, strengthened:** `stackctl up` (and `check`) refuses unless a full scan of the exact pinned set is green or fully excepted and not older than 24 hours. "The exact pinned set" means every image the assembled stack runs, `api`, `bff` and `migrator` included (evaluated strictly, as on the release path). The gate covers every `stackctl` command that creates or starts containers, not only `up` (G3 M-3 has the detail). This is in addition to today's item-4 refusal (`stackctl.py:1325-1329` for `up`, `:1186` for `check`), for as long as item 4 exists. The deploy is where risk starts, so the full gate lives there.
5. **Exceptions keep their rules** (approval in Marco's words, reachability argument, expiry, S-120-07 bounds, D-2 and its two narrowings), but they are needed at release and deploy, and for drift past its deadline, not to unblock unrelated pull requests.

### Consequences

- Good:
  - A pull request fails only for what it introduces; this week's guava, curl, brotli and rebuild churn would have produced one drift issue with deadlines instead of three issue chains, a waiver and 30-odd exceptions.
  - Drift gets an owner and a deadline instead of an emergency.
  - The strict gate moves to where exposure starts (deploy) and is stronger there (fresh full scan required).
- Bad:
  - `main` can carry a known High or Critical for up to its deadline (7/14/30 days). Today it carries them too, but blocked; the difference is that work continues meanwhile. Mitigated by the deadlines and the deploy gate.
  - A pin-changing pull request scans twice (base and head): more minutes (free on the public repository; to be costed if it turns private, N-007).
  - The deploy gate depends on a fresh scan, so a deploy needs network access to the scanner database at deploy time (or a scan artefact not older than 24 hours).
  - The scheduled run needs `issues: write` for the tracking issue: a workflow-permission change (full tier, G3).
  - Gate-change comparison: the pull request's own code runs the head scan, so the defence against a gate that under-reports is review, unchanged from today; a scanner bump that finds more at the head still goes red (fails closed).
- Enforced by (red tests, designed in the implementation issue's G2 and G3 and written by its G5; the architecture note lists the owners):
  - `.claude/tests/test_image_scan.py`: the delta comparison (a new High or Critical at the head fails; a finding present at both passes; an unchanged alias is not scanned; an alias new at the head is gated on all its findings; a gate-change pull request (scan code, scanner pin or `ci.yml`) fails on a finding new at the head in any alias, passes on findings present at both, scans the base with the base's scan code and scanner, and fails closed when the base scan fails; an `exceptions.json`-only change runs the validator and makes no scanner call; a failed or untrusted base or head scan fails closed); the deadline constants by exact value (Critical 7, High 30, High on the LAN request path 14);
  - the workflow tests (`.claude/tests/test_ruleset.py` and the `ci.yml` tests): `image-scan` stays a required check with no job-level `if`; the daily schedule; `issues: write` only on the tracking job, never on a job that runs pull-request code; the tracking step uses `gh` with the job token and no third-party action;
  - `deploy/tests/test_stackctl.py`: the freshness check with red tests (no scan, a scan older than 24 hours, a red scan, a scan of a different pinned set or one missing `api`, `bff` or `migrator`, a non-excepted finding of any age, each refusing before any Docker or Compose call, in every command that creates or starts containers; `check` reports the same problem);
  - unchanged: the existing `test_image_scan.py` and `test_stackctl.py` tests of D-2, items 3 and 4, and the release path.

## What changes for the open work

- The paused chain (#213 → #214 → #215): under option 2 its pull request would be gated only on the images it changes (Postgres, Caddy) against `main`, so pre-existing and newly published findings would move to the drift issue. Its many bridge entries would no longer be needed to merge; they would matter only for a deploy.
- #187's bridge, #195's waiver and #212's exception remain valid records; whether to keep or drop them after the switch is decided in the implementation issue.

## Rollout

1. Done: this issue (#216): G2 architect, G3 security review, then Marco accepted this ADR (2026-10-30).
2. If accepted: one implementation issue (full tier: it changes a required check and workflow permissions): `image_scan.py` delta mode with tests, the daily schedule and the tracking step in CI, the `stackctl` freshness check with red tests, and the pointer lines in ADR-0015 and ADR-0021; its "Done when" carries G3 M-2 to M-5.
   - The implementation pull request is itself gated by the gate-change comparison of item 1. This is a bootstrap: the pull request's own run executes its own `ci.yml`, so the comparison logic under review gates its own merge. The controls are the base scan running `main`'s code (an M-2 detail for the implementation issue), G6 on the diff, and Marco's review. ADR-0015 still applies to every other pull request until the merge. No bypass.
3. Then the chain resumes under the new gate, after the implementation PR merges (ADR-0015 applies until then).
