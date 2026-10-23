# Phase 0 – Image bump for zlib CVE-2026-85091 and Caddy curl CVE-2026-92392

## Issue #183 – Traceability (G5)

No Gherkin acceptance criteria file exists for this issue; the rows below map the issue's "Done when"
items and the G3 threat-delta mitigations (M-1 to M-4) to tests or manual evidence. Tests are in
`tests/Decisya.Identity.Tests/ContainerImageParityTests.cs` (class `ContainerImageParityTests`, trait
`Unit`) unless stated.

## Traceability

| Criterion | Test or evidence |
| --- | --- |
| Done when 1: `image-scan` passes on the PR with the new digests | CI `image-scan` run on the PR (run id recorded in the manifest after the PR run, pending). Local scan exit 0 per the G4 evidence. `manual`: the scan needs the registry and Docker, and no unit test can stand in for it. |
| Done when 2: no zlib CVE-2026-85091 exception remains | `The_exceptions_file_has_no_zlib_CVE_2026_85091_entry_and_one_approved_30_day_caddy_curl_CVE_2026_92392_entry` (new; red on git HEAD, which carried three such entries) |
| Done when 3: CVE-2026-92392 covered by an approved, expiring exception with evidence | Same new test: exactly one entry for image caddy, package curl, issue #183; `approved by Marco <date>` exactly once; no `PENDING`; expiry at most 30 days after added and no later than 2026-11-21. Evidence text (reachability argument) is in the entry's justification; reviewed in G6. |
| Done when 4a: `ContainerImageParityTests` pass | `The_sandbox_image_list_has_a_postgres_alias_matching_ContainerImages`, `The_sandbox_image_list_has_a_redis_alias_matching_ContainerImages`, `The_sandbox_image_list_has_a_caddy_alias_matching_ContainerImages`, `Every_pinned_digest_is_64_lowercase_hex_characters` (all existing; the bumped constants must match `.devcontainer/engine/images.Dockerfile`) |
| Done when 4b: Compose drift check passes | CI drift-check step (compares the committed `deploy/compose/docker-compose.yaml` with the generated one). Marco's regeneration is in the G4 evidence. `manual` locally: regeneration needs Marco's tooling run. |
| G3 M-1: no zlib exception remains; curl exception is single, approved, at most 30 days | The new test above |
| G3 M-2: curl evidence (libcurl dependency check, nothing runs curl) | `manual`: the evidence is the command output recorded in the G4 evidence and summarised in the exception's justification; it needs the image pulled and run with no network. |
| G3 M-3: the #142 Caddy Go entries stay untouched | `.claude/tests/test_image_scan.py` `test_the_committed_142_entries_are_caddy_item_4_entries_only` (existing, verified present; checks 16 caddy entries, added 2026-10-17, expiring by 2026-10-31, approved). The PR diff also shows no change to those entries. The new test covers the curl entry in the same file. |
| G3 M-4: parity, drift and scan agree on the new digests; timing of the merge | Parity tests (row 4a), CI drift check (row 4b), CI `image-scan` (row 1, pending). Timing is `manual`: merge after 2026-10-29 01:11 UTC and before 2026-10-31 (the item 4 fallback date). |

## Local run (G5)

- `dotnet build -warnaserror`: succeeded, 0 warnings, 0 errors.
- `dotnet test --project tests/Decisya.Identity.Tests --filter-not-trait "Category=Integration"`: total 247, succeeded 247, failed 0, skipped 0.

Not run by this gate: Python `.claude` suite, deploy tests, CI `image-scan` and drift check (owned by CI and Marco).

<!-- gate: G5 | verdict: PASS | issue: #183 -->
